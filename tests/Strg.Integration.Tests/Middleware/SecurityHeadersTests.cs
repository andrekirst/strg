using System.Net;
using FluentAssertions;
using Microsoft.Net.Http.Headers;
using Strg.Core.Constants;
using Strg.Integration.Tests.Auth;
using Xunit;

namespace Strg.Integration.Tests.Middleware;

/// <summary>
/// STRG-010 TC-002 + AC3/AC4 + Security Review Checklist pins. Drives the real ASP.NET Core
/// pipeline through <see cref="StrgWebApplicationFactory"/> so the assertions cover the
/// production middleware wiring (<c>UseStrgSecurityHeaders</c> before <c>UseStrgOpenApi</c>
/// and the <c>/dav</c> map, <c>ConfigureKestrel(AddServerHeader=false)</c>) — NOT a hand-rolled
/// minimal host.
/// </summary>
public sealed class SecurityHeadersTests(StrgWebApplicationFactory factory) : IClassFixture<StrgWebApplicationFactory>
{
    /// <summary>
    /// TC-002 + AC3 — <c>X-Content-Type-Options: nosniff</c> is present on every response,
    /// including short-circuiting ones (Swagger spec, health probes, the anonymous token
    /// endpoint failure path). The theory probes multiple surfaces so a regression that
    /// narrows the middleware's reach to only one branch fails here.
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
            $"'{path}' response must carry X-Content-Type-Options per STRG-010 AC3");
        nosniff!.Single().Should().Be("nosniff");

        response.Headers.TryGetValues(HeaderNames.XFrameOptions, out var frameOptions).Should().BeTrue(
            $"'{path}' response must carry X-Frame-Options per STRG-010 AC4");
        frameOptions!.Single().Should().Be("DENY");

        response.Headers.TryGetValues(StrgHeaderNames.ReferrerPolicy, out var referrer).Should().BeTrue(
            $"'{path}' response must carry Referrer-Policy");
        referrer!.Single().Should().Be("strict-origin-when-cross-origin");

        response.Headers.TryGetValues(StrgHeaderNames.PermissionsPolicy, out var permissions).Should().BeTrue(
            $"'{path}' response must carry Permissions-Policy");
        permissions!.Single().Should().Contain("camera=()",
            "the Permissions-Policy value locks down camera/microphone/geolocation per STRG-010");
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
    /// </summary>
    [Fact]
    public async Task Openapi_spec_response_carries_security_headers()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues(HeaderNames.XContentTypeOptions).Single().Should().Be("nosniff");
        response.Headers.GetValues(HeaderNames.XFrameOptions).Single().Should().Be("DENY");
    }
}
