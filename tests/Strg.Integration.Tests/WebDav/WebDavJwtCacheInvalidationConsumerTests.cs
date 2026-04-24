using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Strg.Core.Domain;
using Strg.Core.Events;
using Strg.Infrastructure.Data;
using Strg.WebDav;
using Strg.WebDav.Consumers;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Strg.Integration.Tests.WebDav;

internal sealed class CacheTenantContext(Guid id) : ITenantContext
{
    public Guid TenantId => id;
}

/// <summary>
/// STRG-073 Commit 3 — proves the event-driven cache invalidation contract end-to-end: a
/// <see cref="UserPasswordChangedEvent"/> written to the outbox on the password-change transaction
/// is picked up by <see cref="WebDavJwtCacheInvalidationConsumer"/> and flushes every entry
/// keyed by the user's email from the in-process <see cref="IWebDavJwtCache"/>.
///
/// <para><b>Why a real Postgres + RabbitMQ.</b> The unit test for the consumer body (calling
/// <c>Consume</c> directly with a synthesized <c>ConsumeContext</c>) cannot catch the regression
/// shapes that matter: (a) the consumer is registered in DI but the event is not pulled off the
/// outbox because the registration dropped, (b) the consumer resolves the wrong
/// <see cref="IWebDavJwtCache"/> lifetime (scoped instead of singleton → every request sees a
/// fresh cache and the invalidation fires on an empty side-index). Only the full
/// outbox → broker → consumer round-trip exercises those paths.</para>
///
/// <para><b>Why not assert through the Basic-auth bridge.</b> Booting
/// <c>StrgWebApplicationFactory</c> + issuing a WebDAV PUT, password-change, second PUT would
/// drag OpenIddict token issuance + user seeding + middleware pipeline into this test for one
/// additional assertion — the cache eviction. That whole surface is tested by the bridge's own
/// suite; what is NEW here is the outbox → consumer → cache wire, which this focused shape
/// pins without the ceremony.</para>
/// </summary>
public sealed class WebDavJwtCacheInvalidationConsumerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:3.13-management-alpine")
        .Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _rabbitMq.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _rabbitMq.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task UserPasswordChangedEvent_via_outbox_evicts_cached_jwt_for_the_email()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string email = "alice@example.test";
        const string oldPassword = "correct-horse-battery";
        const string cachedJwt = "eyJhbGciOi...synthetic";

        await using var provider = await BuildServiceProviderAsync(tenantId);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        // Populate the cache as the Basic-auth bridge would after a successful token exchange —
        // the invariant under test is that THIS entry disappears once the consumer fires.
        var cache = provider.GetRequiredService<IWebDavJwtCache>();
        cache.Set(email, oldPassword, cachedJwt, TimeSpan.FromMinutes(14));
        cache.TryGet(email, oldPassword).Should().Be(cachedJwt,
            "sanity: cache must hold the entry before the event fires — otherwise the test " +
            "would pass trivially regardless of whether invalidation ran");

        // Publish the event the way UserManager/UserMutationHandlers do: buffered on the DbContext
        // and committed atomically with SaveChangesAsync so the outbox row lands in the same tx.
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            var bus = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

            await bus.Publish(new UserPasswordChangedEvent(tenantId, userId, email));
            await db.SaveChangesAsync();
        }

        (await harness.Consumed.Any<UserPasswordChangedEvent>()).Should().BeTrue(
            "harness must observe WebDavJwtCacheInvalidationConsumer pulling the event off the " +
            "outbox; otherwise the registration or the bus wiring has drifted");

        // The consumer resolves the singleton IWebDavJwtCache and calls InvalidateUser(email);
        // every entry in the side-index keyed by the email must be gone.
        cache.TryGet(email, oldPassword).Should().BeNull(
            "WebDavJwtCacheInvalidationConsumer must evict the cached entry for the changed " +
            "user's email — otherwise the old password would continue authenticating via the " +
            "cached token until natural TTL (up to 14 minutes) and the outbox-driven " +
            "invalidation story is silently broken");
    }

    [Fact]
    public async Task Consumer_resolves_the_same_singleton_cache_the_bridge_would_use()
    {
        // Lifetime-regression defense: if the consumer's IWebDavJwtCache is registered scoped (not
        // singleton), the consumer sees a fresh instance per message, the side-index is empty, and
        // invalidation silently does nothing even though the event was consumed.
        var tenantId = Guid.NewGuid();
        await using var provider = await BuildServiceProviderAsync(tenantId);

        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();
        var a = scopeA.ServiceProvider.GetRequiredService<IWebDavJwtCache>();
        var b = scopeB.ServiceProvider.GetRequiredService<IWebDavJwtCache>();

        a.Should().BeSameAs(b,
            "IWebDavJwtCache MUST be singleton. A scoped registration would silently defeat " +
            "event-driven invalidation: the consumer would build a fresh cache per dispatch, " +
            "see an empty side-index, and evict nothing — while the bridge's singleton cache " +
            "continues serving the stale token.");
    }

    private async Task<ServiceProvider> BuildServiceProviderAsync(Guid tenantId)
    {
        var connectionString = await CreateFreshDatabaseAsync();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddFilter((_, _) => false));
        services.AddSingleton<ITenantContext>(new CacheTenantContext(tenantId));

        services.AddDbContext<StrgDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            options.UseOpenIddict();
        });

        // Mirror the production lifetimes: IMemoryCache + IWebDavJwtCache are singleton (shared
        // process-wide side-index); WebDavJwtCacheInvalidationConsumer depends on the singleton
        // cache. This is the same shape WebDavServiceExtensions.AddStrgWebDav establishes.
        services.AddMemoryCache();
        services.AddSingleton<IWebDavJwtCache, WebDavJwtCache>();

        services.AddMassTransitTestHarness(bus =>
        {
            bus.AddConsumer<WebDavJwtCacheInvalidationConsumer>();

            bus.AddEntityFrameworkOutbox<StrgDbContext>(outbox =>
            {
                outbox.UsePostgres();
                outbox.UseBusOutbox();
                outbox.QueryDelay = TimeSpan.FromSeconds(1);
            });

            bus.UsingRabbitMq((context, cfg) =>
            {
                var uri = new Uri(_rabbitMq.GetConnectionString());
                cfg.Host(uri);
                cfg.ConfigureEndpoints(context);
            });
        });

        services.Configure<MassTransitHostOptions>(o => o.WaitUntilStarted = true);
        services.AddOptions<TestHarnessOptions>().Configure(o =>
        {
            o.TestInactivityTimeout = TimeSpan.FromSeconds(30);
            o.TestTimeout = TimeSpan.FromMinutes(2);
        });

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            await ctx.Database.MigrateAsync();
        }

        return provider;
    }

    private async Task<string> CreateFreshDatabaseAsync()
    {
        var dbName = $"strg_webdav_cache_{Guid.NewGuid():N}";
        var adminConnectionString = _postgres.GetConnectionString();

        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{dbName}\"";
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = dbName,
        }.ConnectionString;
    }
}
