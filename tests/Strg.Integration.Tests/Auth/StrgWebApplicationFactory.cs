using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MassTransit.EntityFrameworkCoreIntegration;
using OpenIddict.Server.AspNetCore;
using Npgsql;
using Strg.Core.Domain;
using Strg.Core.Services;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Services;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Strg.Integration.Tests.Auth;

/// <summary>
/// End-to-end HTTP test harness for the real ASP.NET Core pipeline in <c>Strg.Api</c>. One
/// container + one database per test class (via <see cref="IClassFixture{TFixture}"/>), matching
/// the cadence memorised in project_phase12_decisions.md.
///
/// Design notes:
/// <list type="bullet">
///   <item><description>Environment is forced to <c>Development</c> so OpenIddict uses ephemeral
///     signing keys and GraphQL subscriptions use the in-memory provider — the production paths
///     require certs and Redis, neither of which are available in CI.</description></item>
///   <item><description>Schema is created directly against the container in <c>InitializeAsync</c>
///     before the host boots. This lets <c>OpenIddictSeedWorker</c> find its tables on first
///     hosted-service startup, and lets the test pre-seed a tenant + admin user so
///     <c>FirstRunInitializationService</c> sees users and no-ops (instead of minting a SuperAdmin
///     with a random password that would never reach the test).</description></item>
///   <item><description>Tests call <see cref="PostTokenAsync"/> against the real
///     <c>/connect/token</c> endpoint. No mocking of the identity stack.</description></item>
/// </list>
/// </summary>
public sealed class StrgWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string DefaultClientId = "strg-default";
    public const string AdminEmail = "admin@strg.test";
    public const string AdminPassword = "integration-test-password-42";
    public const string AdminScopes = "files.read files.write files.share tags.write admin";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    // RabbitMQ container provisioned per-fixture so AddStrgMassTransit's bus startup connects
    // cleanly instead of retrying against `guest@localhost:5672/` for ~30 seconds per affected
    // test class. WithUsername/WithPassword pinned to `guest/guest` so the container's credentials
    // match `appsettings.Development.json`'s defaults — sidesteps the `WebApplicationFactory<T>`
    // configuration-precedence chain (where appsettings.Development.json wins over
    // AddInMemoryCollection for at least some keys, surfaced during STRG-034 fixture work).
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:3.13-management-alpine")
        .WithUsername("guest")
        .WithPassword("guest")
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;
    public Guid AdminTenantId { get; private set; }
    public Guid AdminUserId { get; private set; }

    async Task IAsyncLifetime.InitializeAsync()
    {
        // Parallel container start — saves ~3-5s on cold runs since both containers' image-pull
        // and start phases are independent.
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());
        // Per-factory DB: the default Testcontainers database is reused, but we create a dedicated
        // one so parallel test classes cannot collide on OpenIddict client rows.
        var dbName = $"strg_it_{Guid.NewGuid():N}";
        var adminConnectionString = _postgres.GetConnectionString();
        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
            await cmd.ExecuteNonQueryAsync();
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = dbName,
        }.ConnectionString;

        await BootstrapSchemaAndSeedAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _rabbitMq.DisposeAsync();
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development gives us: ephemeral OpenIddict keys (no X.509 cert), in-memory GraphQL
        // subscriptions (no Redis). Both production paths would crash the host at startup without
        // real infra.
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,

                // RabbitMQ from the testcontainer. Without this, AddStrgMassTransit defaults to
                // localhost:5672 (the dev fallback) and the bus retries forever in the background
                // — the `guest@localhost:5672/` retry storm and occasional ObjectDisposedException
                // testhost crash that drowned full-suite runs before this fix.
                // RabbitMQ:Port is the optional production-code key (read only when set) added in
                // src/Strg.Infrastructure/Messaging/MassTransitExtensions.cs to support
                // Testcontainers' random host port mapping. Username/Password aren't overridden
                // here because the container's `guest/guest` already matches appsettings defaults.
                ["RabbitMQ:Host"] = _rabbitMq.Hostname,
                ["RabbitMQ:Port"] = _rabbitMq.GetMappedPublicPort(5672).ToString(),
                // Disable publisher confirmations in tests to dodge the RabbitMQ.Client 7.x
                // SemaphoreSlim disposal race that crashes the testhost when the bus is torn
                // down mid-run. Outbox-based dispatch (UseBusOutbox) provides durability above
                // this layer, so disabling confirmations is semantically a no-op for tests.
                ["RabbitMQ:PublisherConfirmation"] = "false",
                // Disable MassTransit's InboxCleanupService background loop in tests. Default
                // 1-minute polling races the test-host shutdown's DbContext disposal — the
                // in-flight cleanup query gets an EndOfStreamException-wrapped "transient
                // failure" logged as `[ERR] CleanUpInboxState faulted`. Tests don't generate
                // enough inbox state to need cleanup, so disabling is semantically free.
                ["RabbitMQ:DisableInboxCleanup"] = "true",

                // STRG-074 #152 — NO `OpenIddict:Issuer` override here, by design. Earlier
                // iterations of this factory injected `http://localhost/` as a pin, which masked
                // the production bug where the same key is absent from `appsettings.json` and the
                // Configure<IConfiguration> block silently no-op'd, leaving /dav 401'd on every
                // bearer. Self-detect from IServerAddressesFeature (ServerAddressNormalizer)
                // produces the same value TestServer is bound to, and the integration test now
                // exercises the EXACT path a real operator hits. If this hard-code is ever
                // re-introduced, the factory would diverge from production and the silent-401
                // regression would resurface undetected.

                // STRG-010 — raise the rate-limit budgets to "effectively unlimited" by default
                // so existing test classes (BruteForceLockoutTests in particular, which sends
                // 20+ token requests across its three test methods) are not throttled by the
                // production-shaped Auth=10/min and Global=1000/min defaults. Tests that
                // specifically want to exercise the limiter (Middleware/RateLimitingTests)
                // override these via WithWebHostBuilder. The numbers chosen leave the production
                // pipeline structure intact — the limiter still runs, the policies are still
                // attached, only the budgets are wider.
                ["RateLimiting:Auth:PermitLimit"] = "100000",
                ["RateLimiting:Global:PermitLimit"] = "100000",
            });
        });

        builder.ConfigureServices(services =>
        {
            // OpenIddict rejects HTTP requests by default (ID2083) — production-correct. The
            // in-process HttpClient from WebApplicationFactory speaks HTTP, so the token and
            // discovery endpoints would 400 without this override. Tests opt out of transport
            // security; production behavior is unchanged.
            services.PostConfigure<OpenIddictServerAspNetCoreOptions>(options =>
            {
                options.DisableTransportSecurityRequirement = true;
            });

            // STRG-074 #152 — TestServer implements IServer but does NOT populate
            // IServerAddressesFeature.Addresses (the WebDAV bridge tests call this out explicitly
            // at WebDavBasicAuthBridgeTests.cs:96). Production Kestrel populates it during
            // StartAsync after binding. Our Issuer self-detect in OpenIddictConfiguration reads
            // from that feature, so without this populator the self-detect returns null and every
            // /dav bearer request 401s on issuer mismatch — the EXACT silent-401 bug this ticket
            // closes. Populating the feature here makes the integration test exercise the same
            // IServerAddressesFeature → ServerAddressNormalizer → OpenIddictServerOptions.Issuer
            // dataflow a production host runs, which means a regression in ResolveIssuer that
            // reverts to request-BaseUri fallback would surface here as 401 on WebDAV tests.
            services.AddHostedService<TestServerAddressesPopulator>();

            // Belt-and-braces: also strip MassTransit's InboxCleanupService<StrgDbContext> hosted
            // service directly from DI by closed-generic type. AddStrgMassTransit's
            // `RabbitMQ:DisableInboxCleanup=true` path SHOULD already prevent registration, but
            // configuration-precedence quirks in WebApplicationFactory can swallow that flag —
            // and the cleanup loop is the source of the noisy `[ERR] CleanUpInboxState faulted`
            // shutdown-race log. Closed-generic typeof match is the type-safe equivalent of
            // walking the registration list.
            var inboxCleanupRegistration = services.SingleOrDefault(d =>
                d.ImplementationType == typeof(InboxCleanupService<StrgDbContext>));
            if (inboxCleanupRegistration is not null)
            {
                services.Remove(inboxCleanupRegistration);
            }
        });
    }

    // Runs during IHost.StartAsync, BEFORE TestServer starts dispatching requests and therefore
    // before OpenIddictServerOptions is materialized on the first /connect/token call. A
    // Configure<IServer> delegate registered later in the options pipeline will see the populated
    // Addresses collection when it fires.
    private sealed class TestServerAddressesPopulator(IServer server) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            var feature = server.Features.Get<IServerAddressesFeature>();
            if (feature is not null && feature.Addresses.Count == 0)
            {
                // TestServer's conceptual base is http://localhost/ (its default BaseAddress) —
                // using the same value keeps the `iss` claim identical to what TestServer's
                // HttpClient would send as its Host header, so the validation handler sees
                // token.iss == validation.ValidIssuers[0] regardless of PathBase.
                feature.Addresses.Add("http://localhost");
            }
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// POSTs an <c>application/x-www-form-urlencoded</c> body to <c>/connect/token</c> using the
    /// password grant. Wrapper around the real OpenIddict endpoint — no short-circuiting.
    /// </summary>
    public async Task<HttpResponseMessage> PostTokenAsync(
        string username,
        string password,
        string? clientId = null,
        string? scopes = null)
    {
        var client = CreateClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = password,
            ["client_id"] = clientId ?? DefaultClientId,
            ["scope"] = scopes ?? AdminScopes,
        });
        return await client.PostAsync("/connect/token", form);
    }

    /// <summary>
    /// POSTs a refresh-token grant to <c>/connect/token</c>.
    /// </summary>
    public async Task<HttpResponseMessage> PostRefreshAsync(string refreshToken, string? clientId = null)
    {
        var client = CreateClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId ?? DefaultClientId,
        });
        return await client.PostAsync("/connect/token", form);
    }

    public HttpClient CreateAuthenticatedClient(string accessToken)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>
    /// Forces a wrong-password lockout by posting <paramref name="attempts"/> invalid tokens
    /// against the admin account. Returns the number of attempts made (== <paramref name="attempts"/>
    /// on success). Helper for the STRG-083 HTTP-level lockout tests.
    /// </summary>
    public async Task<int> ForceLockoutAttemptsAsync(string email, int attempts)
    {
        for (var i = 0; i < attempts; i++)
        {
            using var response = await PostTokenAsync(email, $"wrong-password-{i}");
            // Discard body; the test asserts behavior on the follow-up call, not per-attempt.
        }
        return attempts;
    }

    public async Task<User> ReloadAdminAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new TestTenantContext(Guid.Empty));
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(ConnectionString).UseOpenIddict());
        await using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var admin = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == AdminUserId);
        return admin;
    }

    /// <summary>
    /// Idempotently inserts a tenant named <c>default</c>. Required by
    /// <c>/api/v1/users/register</c>, which resolves the target tenant by that hard-coded name
    /// (matching <c>FirstRunInitializationService</c>'s seed). The bootstrap tenant is named
    /// <c>integration-test-tenant</c> specifically so registration tests that need the default
    /// tenant missing (to cover the "tenant not seeded" branch) don't see one by accident.
    /// </summary>
    public async Task<Guid> SeedDefaultTenantAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new TestTenantContext(Guid.Empty));
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(ConnectionString).UseOpenIddict());
        await using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var existing = await db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Name == "default");
        if (existing is not null)
        {
            return existing.Id;
        }
        var tenant = new Tenant { Name = "default" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    /// <summary>
    /// Clears the admin user's lockout state (FailedLoginAttempts + LockedUntil). Tests that
    /// force lockouts call this up front so they don't inherit another test's locked state
    /// — the fixture is class-scoped, so admin state bleeds across tests by default.
    /// </summary>
    public async Task ResetAdminLockoutAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new TestTenantContext(Guid.Empty));
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(ConnectionString).UseOpenIddict());
        await using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var admin = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == AdminUserId);
        admin.FailedLoginAttempts = 0;
        admin.LockedUntil = null;
        await db.SaveChangesAsync();
    }

    private async Task BootstrapSchemaAndSeedAsync()
    {
        // A throw-away service container so we can create the schema + insert the admin user
        // WITHOUT booting the real ASP.NET Core host. Required because:
        // (a) `OpenIddictSeedWorker` runs on `IHost.StartAsync` and immediately writes into the
        //     OpenIddict tables — the schema has to exist first.
        // (b) `FirstRunInitializationService` also runs on host start; if no users exist it mints
        //     a SuperAdmin with a random password that never reaches the test. By pre-seeding a
        //     known admin here, that service sees users and short-circuits.
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new TestTenantContext(Guid.Empty));
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(ConnectionString).UseOpenIddict());
        await using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureCreatedAsync();

        AdminTenantId = Guid.NewGuid();
        var tenant = new Tenant { Id = AdminTenantId, Name = "integration-test-tenant" };
        db.Tenants.Add(tenant);

        var admin = new User
        {
            TenantId = AdminTenantId,
            Email = AdminEmail,
            DisplayName = "Integration Admin",
            PasswordHash = hasher.Hash(AdminPassword),
            Role = UserRole.SuperAdmin,
        };
        db.Users.Add(admin);

        await db.SaveChangesAsync();
        AdminUserId = admin.Id;
    }

    public static async Task<(string AccessToken, string? RefreshToken)> ReadTokensAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var access = json.GetProperty("access_token").GetString()!;
        var refresh = json.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        return (access, refresh);
    }

    private sealed class TestTenantContext(Guid id) : ITenantContext
    {
        public Guid TenantId => id;
    }
}
