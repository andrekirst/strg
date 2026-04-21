using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Strg.Integration.Tests.Auth;

/// <summary>
/// Minimum viable assertion that the integration-test pipeline itself is correct: host boots,
/// OpenIddict answers the discovery endpoint, pre-seeded admin can exchange password for JWT.
/// Every other test under <c>Auth/</c> assumes this works; if this flaps, fix the harness first.
/// </summary>
public sealed class AuthSmokeTests(StrgWebApplicationFactory factory) : IClassFixture<StrgWebApplicationFactory>
{
    [Fact]
    public async Task Host_boots_and_discovery_endpoint_is_reachable()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/.well-known/openid-configuration");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("issuer").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("token_endpoint").GetString().Should().Contain("/connect/token");
        json.GetProperty("jwks_uri").GetString().Should().Contain("/.well-known/jwks");
    }

    [Fact]
    public async Task Password_grant_for_preseeded_admin_returns_access_token()
    {
        var response = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("token_type").GetString().Should().Be("Bearer");
        json.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();
        json.GetProperty("expires_in").GetInt32().Should().BeGreaterThan(0);
        // Refresh token issuance is the STRG-016 surface — requires `offline_access` on both the
        // request scope and the client permissions. Checked in the STRG-016 tests, not here.
    }
}
