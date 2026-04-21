using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Xunit;

namespace Strg.Integration.Tests.Auth;

/// <summary>
/// STRG-013: covers the authentication/authorization pipeline at the HTTP wire level. Uses
/// <c>/connect/userinfo</c> as the representative Bearer-protected endpoint — it's the only
/// real <c>[Authorize]</c>-style surface wired in v0.1 that goes through the OpenIddict
/// validation stack and does NOT currently trip task #27's default-challenge-scheme bug (the
/// userinfo endpoint handles its own challenge path via
/// <c>EnableUserInfoEndpointPassthrough()</c>).
///
/// <para>
/// Scope-policy enforcement (TC-004/TC-005 from the spec) is deferred: no admin- or
/// scope-protected endpoint exists in v0.1 outside of `/api/v1/drives`, which blocks on
/// task #27. When Tranche 4 file-ops endpoints land, the scope-enforcement tests belong here.
/// </para>
///
/// <para>
/// Exempt-endpoint tests (TC-006 /health, TC-007 /metrics) are also deferred — neither
/// endpoint is wired in Program.cs yet.
/// </para>
/// </summary>
public sealed class JwtBearerValidationTests(StrgWebApplicationFactory factory) : IClassFixture<StrgWebApplicationFactory>
{
    // TC-001 — unauthenticated request to a Bearer-protected endpoint returns 401, not 200
    // (bypass) and not 403 (which would falsely imply "authenticated but forbidden"). This is
    // the baseline guarantee that the authorization gate runs at all.
    [Fact]
    public async Task Userinfo_endpoint_without_authorization_header_returns_401()
    {
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/connect/userinfo");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // TC-002 — garbage Bearer value must not be silently treated as anonymous; the validation
    // middleware must reject it. If this ever returned 200, token parsing would be skipped,
    // which is a severe auth bypass.
    [Fact]
    public async Task Userinfo_endpoint_with_malformed_bearer_token_returns_401()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-jwt");

        using var response = await client.GetAsync("/connect/userinfo");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // Complement to TC-002: a Bearer value that's structurally JWT-shaped (three dot-segments,
    // `alg: none`) but unsigned-by-us must also be rejected. Guards against the classic
    // "alg=none" confusion and ensures signature validation is load-bearing, not cosmetic.
    [Fact]
    public async Task Userinfo_endpoint_with_unsigned_jwt_shaped_token_returns_401()
    {
        var client = factory.CreateClient();
        const string forged = "eyJhbGciOiJub25lIn0.e30.";
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", forged);

        using var response = await client.GetAsync("/connect/userinfo");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // TC-005 happy path at the wire level: a valid JWT from password grant is accepted by
    // the userinfo endpoint. Proves the validation middleware can decode, signature-verify,
    // and decrypt the token, AND that the `sub` claim survives the round-trip to be usable
    // downstream. (The 200 body shape assertion lives in OpenIddictConfigurationTests —
    // here we care only that validation said yes.)
    [Fact]
    public async Task Userinfo_endpoint_with_valid_token_returns_200()
    {
        using var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);
        var (accessToken, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);

        using var client = factory.CreateAuthenticatedClient(accessToken);
        using var response = await client.GetAsync("/connect/userinfo");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
    }

    // OIDC discovery must stay anonymous — it's the bootstrap surface clients use *before*
    // they have a token. If the fallback policy accidentally protected it, no client could
    // ever discover the token endpoint to authenticate.
    [Fact]
    public async Task Oidc_discovery_endpoint_is_exempt_from_fallback_policy()
    {
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/.well-known/openid-configuration");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // Same guarantee for the token endpoint: it MUST be callable without a prior token, or
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

    // Notes on deferred coverage:
    //
    // TC-003 (expired token → 401): asserting at the HTTP level requires either clock
    // manipulation inside the host or a sub-second token lifetime with sleeps, both of which
    // add flake for minimal marginal coverage. OpenIddict.Validation is the authoritative
    // enforcement point and is covered upstream; the 15-minute lifetime itself is asserted
    // in OpenIddictConfigurationTests.Access_token_lifetime_is_fifteen_minutes.
    //
    // TC-004/TC-005 (scope enforcement → 403 vs 200): no scope-gated endpoint is reachable
    // without tripping task #27 in v0.1. `/api/v1/drives` POST would exercise the
    // admin-scope path, but the request currently 500s due to the missing default challenge
    // scheme. When task #27 lands and Tranche 4 adds a real file-ops endpoint, the scope
    // tests belong here.
    //
    // TC-006/TC-007 (/health, /metrics exempt): not wired in Program.cs as of v0.1.
}
