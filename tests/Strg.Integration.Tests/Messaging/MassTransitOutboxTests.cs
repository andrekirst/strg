using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Strg.Core.Domain;
using Strg.Core.Events;
using Strg.Infrastructure.Data;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Strg.Integration.Tests.Messaging;

internal sealed class OutboxTenantContext(Guid id) : ITenantContext
{
    public Guid TenantId => id;
}

/// <summary>
/// Minimal test-side consumer used by <see cref="MassTransitOutboxTests"/> to observe events
/// arriving via the outbox dispatch path. No-op handler: the assertion lives on
/// <see cref="ITestHarness.Consumed"/>, not on any side effect here.
/// </summary>
internal sealed class OutboxTestConsumer :
    IConsumer<FileUploadedEvent>,
    IConsumer<FileDeletedEvent>
{
    public Task Consume(ConsumeContext<FileUploadedEvent> context) => Task.CompletedTask;
    public Task Consume(ConsumeContext<FileDeletedEvent> context) => Task.CompletedTask;
}

/// <summary>
/// STRG-061 TC-001..TC-004: proves the MassTransit EF-Core outbox wiring works end-to-end.
/// Tests write a domain event to the outbox via <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>,
/// then assert the event is dispatched via RabbitMQ to a consumer registered with the
/// <see cref="ITestHarness"/>.
///
/// <para>Phase-12 memory: one container per test class. <see cref="PostgreSqlContainer"/> +
/// <see cref="RabbitMqContainer"/> start once in <see cref="InitializeAsync"/> and are shared
/// across facts; each fact gets a fresh database to isolate schema + outbox state.</para>
///
/// <para>TC-002 (process-restart redelivery) and TC-003 (retry + dead-letter) are deferred to a
/// follow-up task — both need orchestration beyond a single-process MassTransit harness
/// (TC-002 needs bus start/stop with shared DB state; TC-003 needs per-consumer DLX observation).
/// Trackers: STRG-061 follow-up.</para>
/// </summary>
public sealed class MassTransitOutboxTests : IAsyncLifetime
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
    public async Task TC001_Publish_domain_event_via_outbox_delivers_to_consumer()
    {
        // TC-001: publish event → consumer receives. Uses the EF Core outbox so the event is not
        // handed to the transport until SaveChangesAsync commits the transaction.
        var tenantId = Guid.NewGuid();
        await using var provider = await BuildServiceProviderAsync(tenantId);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            var publishEndpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

            // Insert a Tenant row so ANY real domain write would pass the tenant filter — the
            // outbox publish itself doesn't depend on it, but having a committed row makes the
            // "atomic business-tx + outbox" semantics visible if the test is extended.
            ctx.Tenants.Add(new Tenant { Id = tenantId, Name = "outbox-test" });

            await publishEndpoint.Publish(new FileUploadedEvent(
                TenantId: tenantId,
                FileId: Guid.NewGuid(),
                DriveId: Guid.NewGuid(),
                UserId: Guid.NewGuid(),
                Size: 1024,
                MimeType: "text/plain"));

            await ctx.SaveChangesAsync();
        }

        var consumed = await harness.Consumed.Any<FileUploadedEvent>();
        consumed.Should().BeTrue("FileUploadedEvent must be delivered to at least one consumer via the outbox");
    }

    [Fact]
    public async Task TC004_Two_events_in_same_transaction_are_both_delivered()
    {
        // TC-004: atomic outbox + business transaction. Two events published in the same scope
        // are committed to OutboxMessage in the same DB transaction as any business writes, then
        // both dispatched. If only one arrives, the dual-write protection has regressed.
        var tenantId = Guid.NewGuid();
        await using var provider = await BuildServiceProviderAsync(tenantId);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var fileId1 = Guid.NewGuid();
        var fileId2 = Guid.NewGuid();

        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            var publishEndpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

            ctx.Tenants.Add(new Tenant { Id = tenantId, Name = "outbox-multi" });

            await publishEndpoint.Publish(new FileUploadedEvent(
                tenantId, fileId1, Guid.NewGuid(), Guid.NewGuid(), 1024, "text/plain"));
            await publishEndpoint.Publish(new FileDeletedEvent(
                tenantId, fileId2, Guid.NewGuid(), Guid.NewGuid()));

            await ctx.SaveChangesAsync();
        }

        // Check both events landed. Use predicate-free Any<T>() first so the harness' default
        // inactivity timeout applies to each type independently — predicate-form Any has a
        // shorter per-call timeout and can time out before the outbox dispatches both rows
        // (QueryDelay is 1s + broker round-trip).
        var upload = await harness.Consumed.Any<FileUploadedEvent>();
        var delete = await harness.Consumed.Any<FileDeletedEvent>();

        upload.Should().BeTrue("FileUploadedEvent from the joint transaction must be dispatched");
        delete.Should().BeTrue("FileDeletedEvent from the joint transaction must be dispatched");

        // And verify the specific events we published arrived (not stale from another test).
        var uploadMatches = harness.Consumed.Select<FileUploadedEvent>(
            x => x.Context.Message.FileId == fileId1).Any();
        var deleteMatches = harness.Consumed.Select<FileDeletedEvent>(
            x => x.Context.Message.FileId == fileId2).Any();

        uploadMatches.Should().BeTrue("this test's FileUploadedEvent (fileId1) must be among the consumed messages");
        deleteMatches.Should().BeTrue("this test's FileDeletedEvent (fileId2) must be among the consumed messages");
    }

    private async Task<ServiceProvider> BuildServiceProviderAsync(Guid tenantId)
    {
        var connectionString = await CreateFreshDatabaseAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new OutboxTenantContext(tenantId));

        services.AddDbContext<StrgDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            options.UseOpenIddict();
        });

        services.AddMassTransitTestHarness(bus =>
        {
            bus.AddConsumer<OutboxTestConsumer>();

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

        // TestInactivityTimeout default is 1s — too short when a test publishes multiple events
        // through the outbox (each publish costs ~1s polling delay + broker RTT). 30s gives the
        // dispatcher room to drain the outbox without masking real failures.
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
        var dbName = $"strg_outbox_{Guid.NewGuid():N}";
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
