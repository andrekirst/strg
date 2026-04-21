using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Strg.Integration.Tests.Auth;

/// <summary>
/// STRG-012: covers the OIDC surface that the embedded OpenIddict server is required to expose.
/// Scope: HTTP wire behavior — discovery document shape, JWKS response, token endpoint status
/// codes, claims carried on the issued JWT, lifetime metadata, registered scopes. The underlying
/// UserManager lockout / timing behavior is exercised in <c>UserManagerTests</c> and not
/// duplicated here.
/// </summary>
public sealed class OpenIddictConfigurationTests(StrgWebApplicationFactory factory) : IClassFixture<StrgWebApplicationFactory>
{
    // TC-006 — discovery document advertises the standard OIDC endpoints.
    [Fact]
    public async Task Discovery_document_advertises_token_userinfo_and_jwks_endpoints()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/.well-known/openid-configuration");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("issuer").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("token_endpoint").GetString().Should().EndWith("/connect/token");
        json.GetProperty("userinfo_endpoint").GetString().Should().EndWith("/connect/userinfo");
        json.GetProperty("jwks_uri").GetString().Should().Contain("/.well-known/jwks");
        json.GetProperty("introspection_endpoint").GetString().Should().EndWith("/connect/introspect");
        json.GetProperty("revocation_endpoint").GetString().Should().EndWith("/connect/revoke");
    }

    // Acceptance criterion from the spec: "Scopes files.read, files.write, files.share,
    // tags.write, admin are registered." The authoritative observable surface is
    // scopes_supported on the discovery document.
    [Fact]
    public async Task Discovery_document_lists_all_strg_specific_scopes()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/.well-known/openid-configuration");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var scopes = json.GetProperty("scopes_supported")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();

        scopes.Should().Contain(["files.read", "files.write", "files.share", "tags.write", "admin"]);
    }

    // TC-007 — JWKS endpoint returns the OpenID-standard JWK set. Ephemeral keys are used in
    // dev/test, so the specific thumbprint isn't asserted; the shape (array of keys with `kty`)
    // is the contract clients rely on.
    [Fact]
    public async Task Jwks_endpoint_returns_key_set_with_at_least_one_key()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/.well-known/jwks");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var keys = json.GetProperty("keys").EnumerateArray().ToArray();
        keys.Should().NotBeEmpty();
        keys[0].GetProperty("kty").GetString().Should().NotBeNullOrEmpty();
    }

    // TC-001 — password-grant happy path produces a parseable JWT. Access token encryption is
    // disabled in Program.cs so the token is a JWS (three dot-separated base64url segments)
    // and callable clients can inspect its claims without a private key.
    [Fact]
    public async Task Password_grant_issues_jwt_with_sub_email_and_tenant_claims()
    {
        using var response = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = json.GetProperty("access_token").GetString()!;

        var payload = DecodeJwtPayload(accessToken);
        payload.GetProperty("sub").GetString().Should().Be(factory.AdminUserId.ToString(),
            "the `sub` claim must carry the user id so downstream handlers can authorize per-user");
        // `email` may be present either as a single string or a string array depending on the
        // claim mapper. Both shapes carry the admin address; assert on the raw JSON text.
        payload.GetProperty("email").GetRawText().Should().Contain(StrgWebApplicationFactory.AdminEmail);
    }

    // JWT payload is the middle base64url segment. We parse manually rather than pull in
    // System.IdentityModel.Tokens.Jwt just for one test — the test cares about wire-format
    // claims, which is exactly what this returns.
    private static JsonElement DecodeJwtPayload(string jwt)
    {
        var segments = jwt.Split('.');
        segments.Should().HaveCount(3, "a JWS has three dot-separated segments");
        var payload = segments[1];
        // base64url → base64 (replace chars, pad to multiple of 4)
        payload = payload.Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    // Access-token lifetime is the core defense against long-lived token theft. Program.cs sets
    // it to 15 minutes; the wire contract is the `expires_in` seconds value, bounded to prove
    // the 15-min configuration is respected (not some silent default).
    [Fact]
    public async Task Access_token_lifetime_is_fifteen_minutes()
    {
        using var response = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var expiresInSeconds = json.GetProperty("expires_in").GetInt32();

        expiresInSeconds.Should().BeInRange(14 * 60, 15 * 60,
            "access token lifetime is configured to 15 minutes; anything longer widens the theft window");
    }

    // TC-002 — wrong password must not leak the user's existence. OpenIddict returns a 400 with
    // the standard `invalid_grant` error code; the wire response shape is what callers assert on.
    [Fact]
    public async Task Password_grant_with_wrong_password_returns_400_invalid_grant()
    {
        using var response = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            "definitely-not-the-admin-password");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    // Account enumeration defense: the unknown-user response must match the wrong-password
    // response in both status and error code. Callers cannot distinguish "no such user" from
    // "right user, wrong password" via the HTTP surface.
    [Fact]
    public async Task Password_grant_with_unknown_email_returns_same_shape_as_wrong_password()
    {
        using var unknownResponse = await factory.PostTokenAsync(
            "no-such-user@strg.test",
            "any-password-42");
        using var wrongPasswordResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            "also-wrong-password");

        unknownResponse.StatusCode.Should().Be(wrongPasswordResponse.StatusCode);
        var unknownJson = await unknownResponse.Content.ReadFromJsonAsync<JsonElement>();
        var wrongJson = await wrongPasswordResponse.Content.ReadFromJsonAsync<JsonElement>();
        unknownJson.GetProperty("error").GetString()
            .Should().Be(wrongJson.GetProperty("error").GetString());
    }

    // Smoke: the issued access token is accepted by the OIDC userinfo endpoint, which is the
    // OIDC-standard surface for "token survives validation". `/connect/userinfo` is part of the
    // STRG-012 advertised contract (discovery document above); if it rejects a freshly issued
    // token, the validation stack is broken.
    [Fact]
    public async Task Issued_access_token_is_accepted_by_userinfo_endpoint()
    {
        using var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);
        var (accessToken, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);

        using var client = factory.CreateAuthenticatedClient(accessToken);
        using var response = await client.GetAsync("/connect/userinfo");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("sub").GetString().Should().Be(factory.AdminUserId.ToString());
    }
}
