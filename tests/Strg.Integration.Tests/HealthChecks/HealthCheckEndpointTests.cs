using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Strg.Integration.Tests.Auth;
using Xunit;

namespace Strg.Integration.Tests.HealthChecks;

/// <summary>
/// STRG-008 — happy-path coverage for the unauthenticated <c>/health/live</c> and
/// <c>/health/ready</c> endpoints exposed by <c>Strg.Api</c>. Drives the real ASP.NET Core
/// pipeline through <see cref="StrgWebApplicationFactory"/> so the assertions cover the
/// production wiring (<c>AddHealthChecks().AddDbContextCheck&lt;StrgDbContext&gt;</c>,
/// <c>AddCheck&lt;StorageHealthCheck&gt;</c>, both <c>MapHealthChecks</c> calls,
/// <see cref="Strg.Api.HealthChecks.SafeHealthCheckResponseWriter"/>) — not a hand-rolled
/// minimal host. The DB-unreachable variant (TC-002) lives in
/// <see cref="UnreachableDbHealthCheckTests"/> because pointing the real <c>Program.cs</c> at
/// a broken database would make the host fail to start (MassTransit RabbitMQ dial-out,
/// OpenIddict seed worker, FirstRunInitializationService all run on <c>IHost.StartAsync</c>).
/// </summary>
public sealed class HealthCheckEndpointTests(StrgWebApplicationFactory factory) : IClassFixture<StrgWebApplicationFactory>
{
    /// <summary>
    /// TC-003 + AC1 + AC5 — <c>/health/live</c> returns 200 even with no Authorization header.
    /// The endpoint is mapped with <c>Predicate = _ => false</c> so no checks execute, which
    /// is what the K8s liveness probe relies on for "process can serve HTTP" semantics.
    ///
    /// <para>NOTE: production maps <c>/health/live</c> WITHOUT a custom <c>ResponseWriter</c>
    /// (only <c>/health/ready</c> uses <see cref="Strg.Api.HealthChecks.SafeHealthCheckResponseWriter"/>),
    /// so the default writer emits the plain-text body <c>"Healthy"</c> (Content-Type
    /// <c>text/plain</c>). We assert against that contract directly rather than parsing JSON.</para>
    /// </summary>
    [Fact]
    public async Task Get_health_live_without_auth_returns_200()
    {
        using var client = factory.CreateClient();

        // Pin the anonymous-access contract: any future regression that reintroduces an outer
        // RequireAuthenticatedUser fallback BEFORE MapHealthChecks (or removes the
        // .AllowAnonymous() call) would surface here as 401.
        client.DefaultRequestHeaders.Authorization.Should().BeNull(
            "K8s liveness probes cannot present credentials — endpoint must be anonymous");

        using var response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Default response writer (no custom ResponseWriter on /health/live in Program.cs)
        // emits the HealthStatus enum value as plain text. K8s only checks the status code,
        // but pin the body so a future regression that swaps to a leaky writer surfaces here.
        var body = await response.Content.ReadAsStringAsync();
        body.Trim().Should().Be("Healthy",
            "default HealthCheckOptions writer emits the enum name; /health/live runs no checks "
            + "so the aggregate is always Healthy");
    }

    /// <summary>
    /// TC-001 + AC2 + AC4 + AC5 — <c>/health/ready</c> with a reachable database returns 200,
    /// is anonymously reachable, and the JSON body lists both explicitly-registered checks
    /// ("database" and "storage").
    ///
    /// <para><b>Note on aggregate status.</b> The predicate <c>check.Tags.Contains("strg-ready")</c>
    /// excludes MassTransit's auto-registered <c>masstransit-bus</c> check (tagged "ready"
    /// without the "strg-" prefix), so RabbitMQ being absent in the test environment does not
    /// affect this aggregate. Database is Healthy (testcontainer is up) and storage is
    /// Degraded (the integration factory does not seed a default Drive — see
    /// <see cref="StorageHealthCheck"/> behaviour for "no default local drive provisioned").
    /// Degraded → 200 by default in <c>MapHealthChecks</c>, so the aggregate is 200.</para>
    /// </summary>
    [Fact]
    public async Task Get_health_ready_without_auth_returns_200_when_db_reachable()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization.Should().BeNull(
            "K8s readiness probes cannot present credentials — endpoint must be anonymous");

        using var response = await client.GetAsync("/health/ready");

        // TC-001: DB reachable → /health/ready returns 200. The "strg-ready" tag namespace
        // (NOT the more common "ready") deliberately excludes MassTransit's bus check so
        // RabbitMQ outages do not defeat the EF outbox pattern by marking the pod not-ready.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("checks", out var checksElement).Should().BeTrue();
        checksElement.ValueKind.Should().Be(JsonValueKind.Array);

        var checks = checksElement.EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c);

        checks.Should().ContainKey("database",
            "AddDbContextCheck<StrgDbContext>(\"database\", tags: [\"strg-ready\"]) must surface here");
        checks.Should().ContainKey("storage",
            "AddCheck<StorageHealthCheck>(\"storage\", tags: [\"strg-ready\"]) must surface here");
        checks.Should().NotContainKey("masstransit-bus",
            "the strg-ready predicate must exclude MassTransit's auto-registered bus check; "
            + "RabbitMQ availability is not a readiness gate (outbox dispatches when broker recovers)");

        // Each check must carry a status string so K8s log collectors can grep individual
        // subsystem health from the JSON envelope.
        foreach (var (name, check) in checks)
        {
            check.TryGetProperty("status", out var statusElement).Should().BeTrue(
                $"check '{name}' must expose a 'status' field");
            statusElement.GetString().Should().NotBeNullOrEmpty();
        }

        // TC-001 specific assertion: the database check is Healthy because the testcontainer
        // is reachable. Pinned explicitly so a regression in AddDbContextCheck wiring would
        // surface here even if the aggregate happens to stay 200.
        checks["database"].GetProperty("status").GetString().Should().Be("Healthy",
            "the postgres testcontainer is up; AddDbContextCheck must report Healthy");

        // SafeHealthCheckResponseWriter sets Cache-Control: no-store, no-cache so intermediaries
        // (proxies, ingress controllers, sidecars) cannot serve a stale Healthy/Unhealthy answer
        // from cache while the underlying state has flipped. Pin the header so a future writer
        // refactor cannot silently drop it.
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue("probe responses must not be stored");
        response.Headers.CacheControl.NoCache.Should().BeTrue("probe responses must not be cached");
    }

    /// <summary>
    /// Security checklist (STRG-008): <i>"Health check responses do not expose stack traces or
    /// internal paths"</i> + <i>"Response does not reveal database connection string details"</i>.
    /// Pin the happy-path body so a future regression that swaps
    /// <see cref="Strg.Api.HealthChecks.SafeHealthCheckResponseWriter"/> back to
    /// <c>UIResponseWriter.WriteHealthCheckUIResponse</c> (which serializes
    /// <c>Exception.Message</c> + <c>Data</c> dictionaries) would surface here.
    /// </summary>
    [Fact]
    public async Task Get_health_ready_response_does_not_expose_stack_traces_or_internal_paths()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");

        var body = await response.Content.ReadAsStringAsync();
        AssertNoLeakageMarkers(body, "/health/ready");
    }

    /// <summary>
    /// Same security pin as above but for <c>/health/live</c>. The liveness endpoint runs
    /// zero checks so its body is mechanically smaller, but a future contributor adding any
    /// metadata to the writer must not be allowed to leak filesystem paths or
    /// connection-string fragments through this endpoint either.
    /// </summary>
    [Fact]
    public async Task Get_health_live_response_does_not_expose_stack_traces_or_internal_paths()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/live");

        var body = await response.Content.ReadAsStringAsync();
        AssertNoLeakageMarkers(body, "/health/live");
    }

    private static void AssertNoLeakageMarkers(string body, string endpoint)
    {
        // Stack-trace markers — case-insensitive because JSON serializers may emit either
        // PascalCase (default UIResponseWriter shape) or camelCase (newtonsoft contract). We
        // match the substrings as the writer would render them.
        body.Should().NotContainEquivalentOf("stackTrace",
            $"{endpoint} response must not include stack-trace JSON keys");
        body.Should().NotContainEquivalentOf("StackTrace",
            $"{endpoint} response must not include stack-trace JSON keys");
        body.Should().NotContainEquivalentOf("Exception",
            $"{endpoint} response must not include serialized Exception details");

        // Absolute filesystem paths from any common host OS layout. LocalFileSystemProvider
        // exception messages embed full drive root paths; if those ever reached the wire via
        // the writer's exception serialization, this assertion would catch them.
        body.Should().NotContain("/var/", $"{endpoint} response must not include /var/ paths");
        body.Should().NotContain("/home/", $"{endpoint} response must not include /home/ paths");
        body.Should().NotContain("/tmp/", $"{endpoint} response must not include /tmp/ paths");
        body.Should().NotContain("C:\\", $"{endpoint} response must not include C:\\ paths");
        body.Should().NotContain("C:/", $"{endpoint} response must not include C:/ paths");

        // Connection-string fragments. The XML doc on SafeHealthCheckResponseWriter calls out
        // that Npgsql exception messages typically embed Host=... and Username=...; these are
        // the substrings to grep for as the regression pin.
        body.Should().NotContainEquivalentOf("Password",
            $"{endpoint} response must not include connection-string password fragments");
        body.Should().NotContainEquivalentOf("Username",
            $"{endpoint} response must not include connection-string username fragments");
        body.Should().NotContainEquivalentOf("Connection",
            $"{endpoint} response must not include connection-string fragments");
        body.Should().NotContain("Host=",
            $"{endpoint} response must not include Npgsql Host=... connection-string syntax");
    }
}
