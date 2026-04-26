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
using Npgsql;
using OpenIddict.Server.AspNetCore;
using Strg.Core.Domain;
using Strg.Core.Services;
using Strg.Core.Storage;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Services;
using Strg.Infrastructure.Storage;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Strg.Integration.Tests.Upload;

/// <summary>
/// HTTP-level fixture for the STRG-034 TUS upload endpoint. One PostgreSQL container + one
/// dedicated database + one local-FS root directory per test class (per
/// <c>project_phase12_decisions.md</c> "one container per test class").
///
/// <para>The drive is seeded with <c>providerType="local"</c> against a temp directory so
/// uploads exercise the real <see cref="Strg.Infrastructure.Storage.LocalFileSystemProvider"/>
/// (including its <c>AppendAsync</c> implementation — load-bearing for multi-chunk PATCH).</para>
/// </summary>
public class StrgTusUploadFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string TestPassword = "tus-upload-tester-password-42";
    public const string TestScopes = "files.read files.write files.share";

    /// <summary>
    /// 32-byte test KEK (base64). Identical to <c>EncryptedUploadServiceTests.ValidKekBase64</c> —
    /// duplicated here because that constant is private. Public so test classes that bypass the
    /// HTTP path (e.g., decrypt assertions in TC-009) can construct a key provider with the same
    /// value.
    /// </summary>
    public const string TestKekBase64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    // Configure the testcontainer to use guest/guest credentials so AddStrgMassTransit's
    // dev fallback path (and the appsettings.Development.json defaults) connect cleanly without
    // needing my AddInMemoryCollection override to win the configuration-precedence race.
    // Default Testcontainers credentials are rabbitmq/rabbitmq via RABBITMQ_DEFAULT_USER/PASS;
    // overriding them via .WithUsername/.WithPassword aligns the container with strg's dev wiring.
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:3.13-management-alpine")
        .WithUsername("guest")
        .WithPassword("guest")
        .Build();

    private string _tempRoot = string.Empty;

    public string ConnectionString { get; private set; } = string.Empty;
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid DriveId { get; private set; }
    public string UserEmail { get; } = $"tus-tester-{Guid.NewGuid():N}@strg.test";
    public long QuotaBytes { get; private set; }
    public string TempStorageRoot => _tempRoot;

    async Task IAsyncLifetime.InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

        var dbName = $"strg_tus_{Guid.NewGuid():N}";
        var adminConn = _postgres.GetConnectionString();
        await using (var admin = new NpgsqlConnection(adminConn))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
            await cmd.ExecuteNonQueryAsync();
        }
        ConnectionString = new NpgsqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;

        // Per-fixture temp root for the local provider. Cleaned up in DisposeAsync — failures here
        // are logged but not re-thrown (test cleanup must be idempotent).
        _tempRoot = Directory.CreateTempSubdirectory($"strg-tus-it-{Guid.NewGuid():N}").FullName;

        await BootstrapAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup. CI runners reap temp roots eventually; a stranded dir is a
            // disk-pressure annoyance, not a correctness problem.
        }
        await _rabbitMq.DisposeAsync();
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,

                // RabbitMQ from the testcontainer. The container is configured (above) with
                // guest/guest to match strg's dev defaults — only Host + Port need overriding
                // here. Testcontainers exposes RabbitMQ on a random host port, so RabbitMQ:Port
                // (an optional production-code key, read only when set) is what closes the gap
                // between the production wiring and the testcontainer.
                ["RabbitMQ:Host"] = _rabbitMq.Hostname,
                ["RabbitMQ:Port"] = _rabbitMq.GetMappedPublicPort(5672).ToString(),
                // Same SemaphoreSlim race workaround as StrgWebApplicationFactory — see
                // MassTransitExtensions.cs's PublisherConfirmation block for the rationale.
                ["RabbitMQ:PublisherConfirmation"] = "false",
                // Same InboxCleanupService shutdown-race quieting as StrgWebApplicationFactory.
                ["RabbitMQ:DisableInboxCleanup"] = "true",

                // Wide rate-limit budgets so test bursts (TC-005's concurrent uploads especially)
                // don't get throttled by the production-shaped Auth=10/min and Global=1000/min
                // defaults.
                ["RateLimiting:Auth:PermitLimit"] = "100000",
                ["RateLimiting:Global:PermitLimit"] = "100000",
            }));

        builder.ConfigureServices(services =>
        {
            // Mirror StrgWebApplicationFactory: OpenIddict refuses HTTP by default, in-process
            // HttpClient speaks HTTP — opt out so /connect/token works.
            services.PostConfigure<OpenIddictServerAspNetCoreOptions>(opts =>
            {
                opts.DisableTransportSecurityRequirement = true;
            });

            // TestServer doesn't populate IServerAddressesFeature; seed the same value the
            // real OpenIddict issuer self-detect expects (matches the existing factory's pattern).
            services.AddHostedService<TestServerAddressesPopulator>();

            // Replace the env-var-bound IKeyProvider with a test instance using our hardcoded
            // KEK. Without this, EnvVarKeyProvider's parameterless ctor would throw because the
            // env var isn't set in the test process (and setting it globally would race with
            // parallel test classes).
            var existing = services.SingleOrDefault(d => d.ServiceType == typeof(IKeyProvider));
            if (existing is not null)
            {
                services.Remove(existing);
            }
            services.AddSingleton<IKeyProvider>(new EnvVarKeyProvider(TestKekBase64));

            // Belt-and-braces: also strip the InboxCleanupService<StrgDbContext> hosted service
            // directly from DI by closed-generic type. See StrgWebApplicationFactory for the full
            // rationale.
            var inboxCleanupRegistration = services.SingleOrDefault(d =>
                d.ImplementationType == typeof(InboxCleanupService<StrgDbContext>));
            if (inboxCleanupRegistration is not null)
            {
                services.Remove(inboxCleanupRegistration);
            }

            ConfigureServicesOverride(services);
        });
    }

    /// <summary>
    /// Hook for subclasses (e.g., the phase-3 inversion fixture) to swap services after the
    /// fixture's own overrides have applied.
    /// </summary>
    protected virtual void ConfigureServicesOverride(IServiceCollection services)
    {
    }

    private async Task BootstrapAsync()
    {
        // Throw-away DI container — same pattern as StrgWebApplicationFactory. Schema is created
        // before the host boots so OpenIddictSeedWorker / FirstRunInitializationService see the
        // tables on first hosted-service startup.
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new FixedTenantContext(Guid.Empty));
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(ConnectionString).UseOpenIddict());
        await using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureCreatedAsync();

        TenantId = Guid.NewGuid();
        var tenant = new Tenant { Id = TenantId, Name = $"tus-tenant-{TenantId:N}" };
        db.Tenants.Add(tenant);

        // Default quota at 10 MiB — most tests stay under it; TC-003 explicitly seeds a smaller
        // quota for the over-quota path via SetUserQuotaAsync.
        QuotaBytes = 10L * 1024 * 1024;
        var user = new User
        {
            TenantId = TenantId,
            Email = UserEmail,
            DisplayName = "TUS Upload Tester",
            PasswordHash = hasher.Hash(TestPassword),
            Role = UserRole.User,
            QuotaBytes = QuotaBytes,
            UsedBytes = 0,
        };
        db.Users.Add(user);

        var drive = new Drive
        {
            TenantId = TenantId,
            Name = $"tus-drive-{Guid.NewGuid():N}".ToLowerInvariant(),
            ProviderType = "local",
            ProviderConfig = JsonSerializer.Serialize(new { rootPath = _tempRoot }),
            EncryptionEnabled = true,
        };
        db.Drives.Add(drive);

        await db.SaveChangesAsync();
        UserId = user.Id;
        DriveId = drive.Id;
    }

    /// <summary>POSTs the password grant against <c>/connect/token</c> for the seeded user.</summary>
    public async Task<string> AuthenticateAsync()
    {
        using var client = CreateClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = UserEmail,
            ["password"] = TestPassword,
            ["client_id"] = "strg-default",
            ["scope"] = TestScopes,
        });
        using var response = await client.PostAsync("/connect/token", form);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("access_token").GetString()!;
    }

    public HttpClient CreateAuthenticatedClient(string accessToken)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>
    /// Snapshots <see cref="User.UsedBytes"/> from a fresh DbContext — the quota commit lives in
    /// a transaction-scoped DbContext separate from the test's own scope, so re-reading from a
    /// fresh context is the only race-free way to observe the post-commit value.
    /// </summary>
    public async Task<long> ReadUsedBytesAsync()
    {
        await using var ctx = NewDbContext();
        var user = await ctx.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == UserId);
        return user.UsedBytes;
    }

    /// <summary>Sets the user's quota for tests that need a smaller budget (e.g., TC-003).</summary>
    public async Task SetUserQuotaAsync(long quotaBytes)
    {
        await using var ctx = NewDbContext();
        var user = await ctx.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == UserId);
        user.QuotaBytes = quotaBytes;
        QuotaBytes = quotaBytes;
        await ctx.SaveChangesAsync();
    }

    public StrgDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<StrgDbContext>().UseNpgsql(ConnectionString).Options,
            new FixedTenantContext(TenantId));

    /// <summary>
    /// Builds the standard TUS metadata header from base64-encoded UTF-8 values. Mirrors the
    /// production parser's expected shape ("path b64,filename b64,contentType b64").
    /// </summary>
    public static string BuildMetadata(string path, string filename, string mimeType = "application/octet-stream")
    {
        static string Encode(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));
        return $"path {Encode(path)},filename {Encode(filename)},contentType {Encode(mimeType)}";
    }

    /// <summary>
    /// POSTs to <c>/upload?driveId={DriveId}</c> with the standard TUS CREATE headers. Returns the
    /// response so the caller can assert the status code AND extract the <c>Location</c> header.
    /// </summary>
    public async Task<HttpResponseMessage> CreateUploadAsync(
        HttpClient client,
        long uploadLength,
        string metadata)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/upload?driveId={DriveId}");
        request.Headers.Add("Tus-Resumable", "1.0.0");
        request.Headers.Add("Upload-Length", uploadLength.ToString());
        request.Headers.Add("Upload-Metadata", metadata);
        return await client.SendAsync(request);
    }

    /// <summary>
    /// PATCHes <paramref name="bytes"/> to the upload URL. Caller passes the upload URL extracted
    /// from CREATE's <c>Location</c> header.
    /// </summary>
    public async Task<HttpResponseMessage> PatchChunkAsync(
        HttpClient client,
        string uploadUrl,
        long offset,
        byte[] bytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, uploadUrl);
        request.Headers.Add("Tus-Resumable", "1.0.0");
        request.Headers.Add("Upload-Offset", offset.ToString());
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        return await client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> HeadAsync(HttpClient client, string uploadUrl)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, uploadUrl);
        request.Headers.Add("Tus-Resumable", "1.0.0");
        return await client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> DeleteUploadAsync(HttpClient client, string uploadUrl)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, uploadUrl);
        request.Headers.Add("Tus-Resumable", "1.0.0");
        return await client.SendAsync(request);
    }

    private sealed class TestServerAddressesPopulator(IServer server) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            var feature = server.Features.Get<IServerAddressesFeature>();
            if (feature is not null && feature.Addresses.Count == 0)
            {
                feature.Addresses.Add("http://localhost");
            }
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
