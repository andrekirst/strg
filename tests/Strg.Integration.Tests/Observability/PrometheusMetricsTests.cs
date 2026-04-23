using System.Net;
using FluentAssertions;
using Strg.Integration.Tests.Auth;
using Xunit;

namespace Strg.Integration.Tests.Observability;

/// <summary>
/// TC-002 / TC-004 — Prometheus scrape endpoint contract:
/// <list type="bullet">
///   <item><description>TC-002: <c>GET /metrics</c> returns HTTP 200, content-type
///     <c>text/plain</c> with <c>version=0.0.4</c> parameter, and a non-empty body.</description></item>
///   <item><description>TC-004: the request carries no Authorization header and the server still
///     returns 200 — regression pin for the <c>.AllowAnonymous()</c> on
///     <c>MapPrometheusScrapingEndpoint</c>. Without that call the fallback
///     <c>RequireAuthenticatedUser</c> policy would 401 every Prometheus scrape.</description></item>
/// </list>
/// </summary>
public sealed class PrometheusMetricsTests(StrgWebApplicationFactory factory) : IClassFixture<StrgWebApplicationFactory>
{
    // TC-002 + TC-004: unauthenticated GET /metrics must return Prometheus text format.
    [Fact]
    public async Task Get_metrics_without_auth_returns_200_prometheus_text()
    {
        using var client = factory.CreateClient();

        // TC-004 explicit pin: ensure no Authorization header is sent so the assertion
        // fails if .AllowAnonymous() is ever removed from MapPrometheusScrapingEndpoint.
        client.DefaultRequestHeaders.Authorization.Should().BeNull(
            "this test must not send Authorization; it validates the anonymous-access contract");

        using var response = await client.GetAsync("/metrics");

        // TC-002: HTTP status 200
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // TC-002: content-type must be a Prometheus-family exposition format.
        // OpenTelemetry.Exporter.Prometheus.AspNetCore 1.15.x may default to either:
        //   - "text/plain; version=0.0.4; charset=utf-8"                (Prometheus 0.0.4)
        //   - "application/openmetrics-text; version=1.0.0; charset=utf-8" (OpenMetrics 1.0.0)
        // Both satisfy the AC "returns Prometheus-format metrics" because OpenMetrics is the
        // IETF-standardized successor Prometheus scrapers natively accept. We pin BOTH shapes
        // rather than gamble on the beta's negotiation default.
        var contentType = response.Content.Headers.ContentType;
        contentType.Should().NotBeNull();
        contentType!.MediaType.Should().BeOneOf("text/plain", "application/openmetrics-text");

        contentType.Parameters
            .Any(p => p.Name == "version" && (p.Value == "0.0.4" || p.Value == "1.0.0"))
            .Should().BeTrue("Prometheus or OpenMetrics version must be declared in the Content-Type");

        // TC-002: body must be non-empty (scrape endpoint must emit at least some metrics)
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrWhiteSpace("Prometheus scrape endpoint must return metric lines");
    }
}
