using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Strg.Integration.Tests.Auth;
using Xunit;

namespace Strg.Integration.Tests.Middleware;

/// <summary>
/// STRG-010 TC-001 — HTTP requests are redirected to HTTPS. Exercised by injecting a
/// <c>HTTPS_PORT</c> configuration entry via
/// <see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/>. The shared factory
/// deliberately does NOT set <c>HTTPS_PORT</c> so the rest of the integration-test suite
/// continues to run against a plain-HTTP TestServer (<c>UseHttpsRedirection</c> logs a
/// warning and no-ops when no HTTPS port is discoverable) — see STRG-010 advisor note.
/// </summary>
public sealed class HttpsRedirectionTests(StrgWebApplicationFactory factory) : IClassFixture<StrgWebApplicationFactory>
{
    [Fact]
    public async Task Http_request_returns_redirect_to_https()
    {
        using var redirectingFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                // HttpsRedirectionMiddleware reads the target port from (in order):
                // HttpsRedirectionOptions.HttpsPort, ASPNETCORE_HTTPS_PORT env var,
                // HTTPS_PORT config key, then IServerAddressesFeature HTTPS bindings.
                // TestServer binds only HTTP, so this config key is the only path that fires
                // the redirect in the test environment.
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["HTTPS_PORT"] = "443",
                });
            });
        });

        using var client = redirectingFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            // AllowAutoRedirect must be false — default HttpClient follows redirects transparently
            // and the follow-up HTTPS request would fail with ConnectionRefused against TestServer,
            // masking the redirect contract this test is meant to pin.
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://localhost"),
        });

        using var response = await client.GetAsync("/health/live");

        // 307 (TemporaryRedirect) is the ASP.NET Core default; 308 (PermanentRedirect) appears
        // if HttpsRedirectionOptions.RedirectStatusCode is overridden. Pin both so a future
        // options flip does not silently break the AC.
        response.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.TemporaryRedirect, HttpStatusCode.PermanentRedirect },
            "HTTP requests must be redirected to HTTPS per STRG-010 AC1");

        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.Scheme.Should().Be("https",
            "the Location header must point at an https:// URL");
    }
}
