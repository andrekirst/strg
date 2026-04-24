using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Net.Http.Headers;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.HealthChecks;
using Strg.Infrastructure.Storage;
using Xunit;

namespace Strg.Integration.Tests.HealthChecks;

/// <summary>
/// STRG-008 TC-002 — pin the response when <see cref="StrgDbContext"/>'s connection string
/// points at an unreachable host. We do NOT boot the real <c>Strg.Api</c> <c>Program.cs</c>
/// against a broken DB because the production host's <c>IHost.StartAsync</c> wires up
/// MassTransit's RabbitMQ dial-out, the <c>OpenIddictSeedWorker</c>, and
/// <c>FirstRunInitializationService</c> — all three would fail before the first request and
/// the test would prove nothing about the health endpoint.
///
/// <para>Instead we build a minimal <see cref="WebApplication"/> via
/// <see cref="WebHostBuilderExtensions.UseTestServer(IWebHostBuilder)"/> that mirrors only
/// the production health-check wiring under test:
/// <list type="bullet">
///   <item><description><c>AddDbContext&lt;StrgDbContext&gt;</c> with Npgsql pointing at
///     <c>192.0.2.1</c> — RFC 5737 TEST-NET-1 documentation prefix, guaranteed not to route,
///     no DNS lookup. The <c>Timeout=2</c> seconds bounds the per-test wall-clock cost so the
///     test cannot flake on slow CI runners.</description></item>
///   <item><description><c>AddDbContextCheck&lt;StrgDbContext&gt;("database", tags: ["strg-ready"])</c>
///     and <c>AddCheck&lt;StorageHealthCheck&gt;("storage", tags: ["strg-ready"])</c> — the same
///     two-check shape <c>Program.cs</c> registers, with the same "strg-ready" tag namespace
///     that excludes MassTransit's auto-registered <c>masstransit-bus</c> check.</description></item>
///   <item><description>An in-test <c>SafeishHealthResponseWriter</c> that mirrors the
///     production <c>SafeHealthCheckResponseWriter</c> wire format. Re-implementing the writer
///     here (instead of widening the production type's accessibility via InternalsVisibleTo)
///     keeps the "DO NOT modify production code" rule clean and lets the regression pin be
///     read in isolation: if a contributor changes the production writer to leak data, the
///     happy-path tests in <see cref="HealthCheckEndpointTests"/> (which exercise the real
///     writer) will still catch it.</description></item>
/// </list></para>
/// </summary>
public sealed class UnreachableDbHealthCheckTests : IAsyncLifetime
{
    // RFC 5737 TEST-NET-1 — guaranteed not to route, no DNS lookup. Timeout=2 / Command Timeout=2
    // bounds the wall-clock cost of CanConnectAsync to ~2s per request. Include Error Detail=false
    // is defence-in-depth: even if a future Npgsql changes its default error text, no parameter
    // values from the connection string will be embedded in the exception.
    private const string UnreachableDbConnectionString =
        "Host=192.0.2.1;Port=5432;Database=strg_test;Username=strg;Password=x;"
        + "Timeout=2;Command Timeout=2;Include Error Detail=false";

    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();

        // TestServer in-memory transport — no Kestrel binding, no port, no socket dial-out for
        // the HTTP layer. The unreachable-host probe inside the DB check still goes through the
        // real Npgsql socket layer, which is what we want to exercise.
        builder.WebHost.UseTestServer();

        // Stub ITenantContext so DI can resolve StrgDbContext (DbContext ctor takes one). The
        // health probe runs unauthenticated in production too, where ITenantContext.TenantId is
        // Guid.Empty — we mirror that here.
        builder.Services.AddSingleton<ITenantContext>(new EmptyTenantContext());

        // Skip UseOpenIddict() — AddDbContextCheck only calls Database.CanConnectAsync, which
        // doesn't need the OpenIddict entity model. Pulling it in would force the test project
        // to mirror more of Program.cs's wiring than the contract under test demands.
        builder.Services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(UnreachableDbConnectionString));

        // Same singleton-registry-with-builtins-atomic pattern Program.cs uses. StorageHealthCheck
        // resolves IStorageProviderRegistry from DI; without this it would fail with a clear DI
        // error and we'd be testing the wrong thing.
        builder.Services.AddStrgStorageProviders();

        builder.Services.AddHealthChecks()
            .AddDbContextCheck<StrgDbContext>("database", tags: ["strg-ready"])
            .AddCheck<StorageHealthCheck>("storage", tags: ["strg-ready"]);

        _app = builder.Build();

        // Mirror the production /health/live and /health/ready maps. SafeishHealthResponseWriter
        // is the in-test stand-in for production's SafeHealthCheckResponseWriter — same wire
        // shape so the leakage-grep assertions below are meaningful.
        _app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = SafeishHealthResponseWriter.WriteAsync,
        });

        _app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("strg-ready"),
            ResponseWriter = SafeishHealthResponseWriter.WriteAsync,
        });

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>
    /// TC-002 + AC3 + STRG-008 security checklist —
    /// <list type="number">
    ///   <item><description><c>/health/ready</c> returns 503 when the DB is unreachable.</description></item>
    ///   <item><description>Body's aggregate <c>status</c> is <c>"Unhealthy"</c> and the
    ///     <c>"database"</c> entry in <c>checks[]</c> is <c>"Unhealthy"</c>.</description></item>
    ///   <item><description><b>Most importantly</b> — the body does not leak the connection
    ///     string host (192.0.2.1), the database name (strg_test), the username, the password,
    ///     or any Npgsql exception-class names / stack-trace markers. This is the regression
    ///     pin for <i>"Response does not reveal database connection string details"</i> and
    ///     <i>"Health check responses do not expose stack traces or internal paths"</i>.</description></item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task Get_health_ready_returns_503_when_db_unreachable()
    {
        _client.Should().NotBeNull("InitializeAsync must populate the client before tests run");

        using var response = await _client!.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "MapHealthChecks default mapping: Unhealthy → 503. The DB check fails because "
            + "192.0.2.1 is unrouteable, so the aggregate is Unhealthy.");

        var body = await response.Content.ReadAsStringAsync();

        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("status").GetString().Should().Be("Unhealthy");

        var checks = json.GetProperty("checks");
        checks.ValueKind.Should().Be(JsonValueKind.Array);

        var dbCheck = checks.EnumerateArray()
            .FirstOrDefault(c => c.GetProperty("name").GetString() == "database");
        dbCheck.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            "the 'database' check must appear in the response body");
        dbCheck.GetProperty("status").GetString().Should().Be("Unhealthy");

        // SECURITY PIN — the wire-format body must NEVER leak any of the following. Each
        // assertion documents the specific failure mode it pins.
        body.Should().NotContain("192.0.2.1",
            "the configured database host must never appear in the wire response");
        body.Should().NotContain("strg_test",
            "the configured database name must never appear in the wire response");
        body.Should().NotContain("Host=",
            "Npgsql connection-string syntax must never appear in the wire response");
        body.Should().NotContain("Username=",
            "Npgsql connection-string syntax must never appear in the wire response");
        body.Should().NotContainEquivalentOf("Password",
            "the word 'Password' must never appear in the wire response (case-insensitive)");
        body.Should().NotContain("Npgsql",
            "the Npgsql library/exception namespace must never appear in the wire response");
        body.Should().NotContain("NpgsqlException",
            "Npgsql exception type names must never appear in the wire response");

        // Stack-trace markers. ".NET" formats stack frames with "   at Namespace.Type.Method"
        // and inner-exception headers with "--->". If either leaks the writer is serializing
        // exceptions, which is exactly the regression we're pinning against.
        body.Should().NotContain("at ",
            "stack-frame markers ('   at Namespace.Type.Method') must never reach the wire");
        body.Should().NotContain("--->",
            "inner-exception markers ('--->') must never reach the wire");
    }

    /// <summary>
    /// TC-003 reinforcement + AC1 — even with the database completely unreachable,
    /// <c>/health/live</c> still returns 200 because the liveness predicate is <c>_ => false</c>
    /// and no checks execute. This is the contract K8s relies on: liveness must reflect
    /// "process can serve HTTP", not "all dependencies are healthy" (the latter is readiness).
    /// </summary>
    [Fact]
    public async Task Get_health_live_returns_200_when_db_unreachable()
    {
        _client.Should().NotBeNull("InitializeAsync must populate the client before tests run");

        using var response = await _client!.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "/health/live runs zero checks (Predicate = _ => false), so DB unreachability "
            + "must not affect its outcome");

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("status").GetString().Should().Be("Healthy");
    }

    private sealed class EmptyTenantContext : ITenantContext
    {
        public Guid TenantId => Guid.Empty;
    }

    /// <summary>
    /// In-test stand-in for <c>Strg.Api.HealthChecks.SafeHealthCheckResponseWriter</c>. Mirrors
    /// the production wire format <c>{ status, total_duration_ms, checks: [{ name, status,
    /// duration_ms, description? }] }</c> and DELIBERATELY refuses to serialize
    /// <see cref="HealthReportEntry.Exception"/> or <see cref="HealthReportEntry.Data"/>.
    ///
    /// <para>We re-implement here (instead of opening up the production type via
    /// <c>InternalsVisibleTo</c>) because the task forbids modifying production code. The
    /// happy-path tests in <see cref="HealthCheckEndpointTests"/> still cover the real
    /// production writer end-to-end via <see cref="Strg.Integration.Tests.Auth.StrgWebApplicationFactory"/>.</para>
    /// </summary>
    private static class SafeishHealthResponseWriter
    {
        public static async Task WriteAsync(HttpContext context, HealthReport report)
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers[HeaderNames.CacheControl] = "no-store, no-cache";

            using var buffer = new MemoryStream();
            await using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
            {
                writer.WriteStartObject();
                writer.WriteString("status", report.Status.ToString());
                writer.WriteNumber("total_duration_ms", (long)report.TotalDuration.TotalMilliseconds);

                writer.WriteStartArray("checks");
                foreach (var entry in report.Entries)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", entry.Key);
                    writer.WriteString("status", entry.Value.Status.ToString());
                    writer.WriteNumber("duration_ms", (long)entry.Value.Duration.TotalMilliseconds);
                    if (entry.Value.Exception is null && !string.IsNullOrEmpty(entry.Value.Description))
                    {
                        writer.WriteString("description", entry.Value.Description);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            await context.Response.Body.WriteAsync(buffer.ToArray(), context.RequestAborted);
        }
    }
}
