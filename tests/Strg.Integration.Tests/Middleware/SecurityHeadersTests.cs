using System.Net;
using FluentAssertions;
using Microsoft.Net.Http.Headers;
using Strg.Core.Constants;
using Strg.Integration.Tests.Auth;
using Xunit;

namespace Strg.Integration.Tests.Middleware;

/// <summary>
/// STRG-010 TC-002 + AC3/AC4 + STRG-084 TC-001 + Security Review Checklist pins. Drives the
/// real ASP.NET Core pipeline through <see cref="StrgWebApplicationFactory"/> so the
/// assertions cover the production middleware wiring (<c>UseStrgSecurityHeaders</c> before
/// <c>UseStrgOpenApi</c> and the <c>/dav</c> map, <c>ConfigureKestrel(AddServerHeader=false)</c>)
/// — NOT a hand-rolled minimal host.
/// </summary>
public sealed class SecurityHeadersTests(StrgWebApplicationFactory factory) : IClassFixture<StrgWebApplicationFactory>
{
    /// <summary>
    /// STRG-010 TC-002 + STRG-084 TC-001 + STRG-084 AC1/AC2 — every API response carries the
    /// strg security-header set, including short-circuiting ones (Swagger spec, health probes,
    /// the anonymous token endpoint failure path). The theory probes multiple surfaces so a
    /// regression that narrows the middleware's reach to only one branch fails here.
    ///
    /// <para>
    /// The assertion set covers BOTH the STRG-010 baseline (X-Content-Type-Options,
    /// X-Frame-Options, Referrer-Policy, Permissions-Policy) AND the STRG-084 additions
    /// (Content-Security-Policy with <c>default-src 'none'; frame-ancestors 'none';</c> and
    /// <c>X-Permitted-Cross-Domain-Policies: none</c>). Permissions-Policy is checked for
    /// BOTH <c>camera=()</c> (STRG-010 baseline) and <c>interest-cohort=()</c> (STRG-084
    /// FLoC/Topics opt-out) so a regression that drops either token surfaces here.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/metrics")]
    public async Task Get_any_response_has_strg_security_headers(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        // Response may be 200 (health, openapi, metrics) or a business failure — the header
        // contract is the same regardless of status; OnStarting fires before the response
        // body flushes.
        response.Headers.TryGetValues(HeaderNames.XContentTypeOptions, out var nosniff).Should().BeTrue(
            $"'{path}' response must carry X-Content-Type-Options per STRG-010 AC3 / STRG-084 AC1");
        nosniff!.Single().Should().Be("nosniff");

        response.Headers.TryGetValues(HeaderNames.XFrameOptions, out var frameOptions).Should().BeTrue(
            $"'{path}' response must carry X-Frame-Options per STRG-010 AC4 / STRG-084 AC2");
        frameOptions!.Single().Should().Be("DENY");

        response.Headers.TryGetValues(StrgHeaderNames.ReferrerPolicy, out var referrer).Should().BeTrue(
            $"'{path}' response must carry Referrer-Policy");
        referrer!.Single().Should().Be("strict-origin-when-cross-origin");

        response.Headers.TryGetValues(StrgHeaderNames.PermissionsPolicy, out var permissions).Should().BeTrue(
            $"'{path}' response must carry Permissions-Policy");
        var permissionsValue = permissions!.Single();
        permissionsValue.Should().Contain("camera=()",
            "the Permissions-Policy value locks down camera/microphone/geolocation per STRG-010");
        permissionsValue.Should().Contain("interest-cohort=()",
            "STRG-084 adds the FLoC/Topics opt-out token to Permissions-Policy");

        // STRG-084 — strict CSP applied to every response. The pin checks for the literal
        // value because both directives are load-bearing: default-src 'none' blocks every
        // resource fetch, frame-ancestors 'none' duplicates X-Frame-Options for browsers
        // that honour CSP but not the legacy header. A relaxed default-src would silently
        // pass a startsWith check, hence the full-string assertion.
        response.Headers.TryGetValues(HeaderNames.ContentSecurityPolicy, out var csp).Should().BeTrue(
            $"'{path}' response must carry Content-Security-Policy per STRG-084 spec");
        csp!.Single().Should().Be("default-src 'none'; frame-ancestors 'none';",
            "STRG-084 fixes the API-host CSP to default-src 'none' + frame-ancestors 'none'");

        // STRG-084 — defence-in-depth against legacy Flash/Acrobat plug-ins that consult
        // crossdomain.xml.
        response.Headers.TryGetValues(StrgHeaderNames.XPermittedCrossDomainPolicies, out var crossDomain).Should().BeTrue(
            $"'{path}' response must carry X-Permitted-Cross-Domain-Policies per STRG-084 spec");
        crossDomain!.Single().Should().Be("none");
    }

    /// <summary>
    /// Security Review Checklist: "<c>Server</c> header is removed (Kestrel default)". The
    /// suppression lives at the host level via <c>ConfigureKestrel(AddServerHeader=false)</c>
    /// in <c>Program.cs</c> — the security-headers middleware alone can't do this because
    /// Kestrel writes the Server header at the connection layer, AFTER user
    /// <c>HttpResponse.OnStarting</c> callbacks. This test pins that the suppression is
    /// actually wired.
    /// </summary>
    [Fact]
    public async Task Response_does_not_leak_server_header()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        // HttpResponseHeaders treats "Server" as a typed header and rejects raw
        // Contains/TryGetValues calls — use the typed accessor. An empty Server collection
        // means the header is absent on the wire, which is the contract Kestrel's
        // AddServerHeader=false produces.
        response.Headers.Server.Should().BeEmpty(
            "Kestrel default Server header must be suppressed via AddServerHeader=false");
    }

    /// <summary>
    /// Security Review Checklist: "<c>X-Powered-By</c> header is removed". Kestrel does not
    /// emit this by default; the strip in <see cref="Strg.Api.Security.SecurityHeadersMiddleware"/>
    /// is defence-in-depth against reverse proxies or downstream middleware that might inject it.
    /// </summary>
    [Fact]
    public async Task Response_does_not_leak_x_powered_by_header()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        response.Headers.Contains(HeaderNames.XPoweredBy).Should().BeFalse();
        response.Content.Headers.Contains(HeaderNames.XPoweredBy).Should().BeFalse();
    }

    /// <summary>
    /// Regression pin: the security-headers middleware is registered BEFORE
    /// <c>UseStrgOpenApi</c> so the Swashbuckle short-circuit response (spec JSON) still
    /// carries the full header set. Swashbuckle writes the response synchronously and the
    /// <c>OnStarting</c>-based middleware is the only placement that survives that pattern.
    /// STRG-084 widens this pin to the CSP + X-Permitted-Cross-Domain-Policies additions so a
    /// future contributor who moves the security-headers middleware AFTER the OpenAPI
    /// middleware would surface that regression specifically on this short-circuit path.
    /// </summary>
    [Fact]
    public async Task Openapi_spec_response_carries_security_headers()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues(HeaderNames.XContentTypeOptions).Single().Should().Be("nosniff");
        response.Headers.GetValues(HeaderNames.XFrameOptions).Single().Should().Be("DENY");
        response.Headers.GetValues(HeaderNames.ContentSecurityPolicy).Single()
            .Should().Be("default-src 'none'; frame-ancestors 'none';");
        response.Headers.GetValues(StrgHeaderNames.XPermittedCrossDomainPolicies).Single()
            .Should().Be("none");
    }
}
