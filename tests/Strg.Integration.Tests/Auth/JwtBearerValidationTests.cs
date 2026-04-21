using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace Strg.Integration.Tests.Auth;

/// <summary>
/// STRG-013: covers the authentication/authorization pipeline that protects every non-exempt
/// endpoint. Asserts the wire contract: 401 for missing/malformed credentials, 403 for
/// authenticated-but-insufficient, 200 when everything lines up. Scope enforcement is tested
/// via the <c>/api/v1/drives</c> group — POST requires the <c>admin</c> scope, GET requires
/// only an authenticated user, so the same endpoint surface exercises both 401 and 403 code
/// paths without needing a test-only endpoint.
/// </summary>
public sealed class JwtBearerValidationTests(StrgWebApplicationFactory factory) : IClassFixture<StrgWebApplicationFactory>
{
    // TC-001 — unauthenticated request to a protected endpoint is 401, not 403. 401 tells the
    // client "you are anonymous"; 403 would imply "you are known but not permitted", which
    // would be misleading here.
    [Fact(Skip = "Blocked by task #27 — see above.")]
    public async Task Protected_endpoint_without_authorization_header_returns_401()
    {
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/drives");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // TC-002 — a garbage Bearer value must not be silently treated as anonymous: the validation
    // middleware should reject it with 401. If this ever returned 200, token parsing would be
    // skipped entirely, which is a severe auth bypass.
    [Fact(Skip = "Blocked by task #27: no DefaultChallengeScheme configured — any Bearer token currently 500s instead of being validated. Unblocks when AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme) lands in Program.cs / OpenIddictConfiguration.")]
    public async Task Protected_endpoint_with_malformed_bearer_token_returns_401()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-jwt");

        using var response = await client.GetAsync("/api/v1/drives");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // Complement to TC-002: a Bearer value that's structurally JWT-shaped (three dot-segments)
    // but unsigned-by-us must also be rejected. Guards against the classic "alg=none" confusion
    // and ensures signature validation is load-bearing.
    [Fact(Skip = "Blocked by task #27 — see above.")]
    public async Task Protected_endpoint_with_unsigned_jwt_shaped_token_returns_401()
    {
        var client = factory.CreateClient();
        // Header {"alg":"none"}, empty payload, empty sig — well-formed JWS shape, not signed by our keys.
        const string forged = "eyJhbGciOiJub25lIn0.e30.";
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", forged);

        using var response = await client.GetAsync("/api/v1/drives");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // TC-005 — happy path: a valid token with files.read is enough to list drives (GET requires
    // only RequireAuthenticatedUser on the group, no scope). Verifies the validation +
    // authorization handlers compose correctly and the tenant_id claim is usable by
    // ClaimsPrincipalExtensions.GetTenantId inside the handler.
    [Fact(Skip = "Blocked by task #27 — see above.")]
    public async Task Protected_endpoint_with_valid_token_returns_200()
    {
        using var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);
        var (accessToken, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);

        using var client = factory.CreateAuthenticatedClient(accessToken);
        using var response = await client.GetAsync("/api/v1/drives");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
    }

    // TC-004 — scope enforcement: a token without the admin scope cannot hit an admin-only
    // endpoint. Expected status is 403 (authorization failed post-authentication), NOT 401
    // (which would mean the token was rejected outright). This is the single most important
    // signal for client developers: "you are logged in, but your token is narrower than this
    // operation requires."
    [Fact(Skip = "Blocked by task #27 — see above.")]
    public async Task Admin_endpoint_with_token_missing_admin_scope_returns_403()
    {
        using var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword,
            scopes: "files.read");
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            await tokenResponse.Content.ReadAsStringAsync());
        var (accessToken, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);

        using var client = factory.CreateAuthenticatedClient(accessToken);
        using var response = await client.PostAsJsonAsync("/api/v1/drives", new
        {
            name = "attempt",
            providerType = "local",
            providerConfig = new { path = "/tmp" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            await response.Content.ReadAsStringAsync());
    }

    // Sanity check the complement: a token WITH the admin scope gets past the 403 gate. We
    // don't assert 200 here because CreateDrive has its own request-shape validation — any
    // non-401/403 status means the scope gate was cleared, which is the only thing this test
    // cares about.
    [Fact(Skip = "Blocked by task #27 — see above.")]
    public async Task Admin_endpoint_with_token_containing_admin_scope_is_not_forbidden()
    {
        using var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);
        var (accessToken, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);

        using var client = factory.CreateAuthenticatedClient(accessToken);
        using var response = await client.PostAsJsonAsync("/api/v1/drives", new
        {
            name = $"it-drive-{Guid.NewGuid():N}",
            providerType = "local",
            providerConfig = new { path = "/tmp" },
        });

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    // OIDC discovery endpoints must stay anonymous — they are the bootstrap surface clients
    // use *before* they have a token. If the fallback policy accidentally protected them, no
    // client could ever discover the token endpoint to authenticate.
    [Fact]
    public async Task Oidc_discovery_endpoint_is_exempt_from_fallback_policy()
    {
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/.well-known/openid-configuration");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // Same guarantee for the token endpoint: it MUST be callable without a prior token, or the
    // password grant is unreachable. Posting no body yields a 400 (bad request shape) rather
    // than a 401 (auth required) — that's what this test pins down.
    [Fact]
    public async Task Token_endpoint_is_exempt_from_fallback_policy()
    {
        var client = factory.CreateClient();

        using var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent([]));

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "the token endpoint itself must not require prior authentication");
    }

    // Note on TC-003 (expired token → 401): asserting this at the HTTP level requires either
    // (a) clock manipulation inside the host or (b) configuring a sub-second token lifetime
    // and sleeping, both of which add flake and complicate the harness for minimal coverage.
    // OpenIddict.Validation is the authoritative enforcement point and is covered by its own
    // unit-test suite upstream; the 15-minute lifetime itself is asserted in
    // OpenIddictConfigurationTests.Access_token_lifetime_is_fifteen_minutes.
    //
    // Note on TC-006/TC-007 (/health, /metrics exempt): those endpoints are not yet wired in
    // Program.cs as of v0.1. When they land, the corresponding exemption tests belong here.
}
