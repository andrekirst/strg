using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Net.Http.Headers;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using Strg.WebDav;
using Xunit;

namespace Strg.Api.Tests.WebDav;

/// <summary>
/// STRG-073 — pins the bridge's security-facing behaviors: the cache TTL must be derived
/// directly from the token endpoint's <c>expires_in</c> minus the 60-second safety margin
/// (mandated unit test), and the failure responses must carry the WebDAV-client-actionable
/// shapes (401 + WWW-Authenticate on bad creds, 502 on upstream failure).
///
/// <para>The tests drive the middleware directly rather than through a full
/// <see cref="Microsoft.AspNetCore.TestHost.TestServer"/> — the contract under test is the
/// bridge's request-handling, not the /dav map wiring (that's the integration test surface in
/// STRG-074).</para>
/// </summary>
public sealed class BasicAuthJwtBridgeMiddlewareTests
{
    [Fact]
    public async Task Cache_ttl_is_access_token_lifetime_minus_60s()
    {
        // Mandated unit test from the STRG-073 spec: cache TTL = expires_in - 60s. We assert by
        // observing the TimeSpan handed to IWebDavJwtCache.Set — that's the single surface the
        // middleware uses to communicate the TTL to the cache, so any drift between the token
        // endpoint's expires_in and the cached lifetime will surface here.
        const int expiresIn = 900; // 15 minutes — the STRG-012 access-token lifetime
        var cache = Substitute.For<IWebDavJwtCache>();
        var factory = new StubHttpClientFactory(new StubTokenHandler(
            HttpStatusCode.OK,
            $$"""{"access_token":"jwt-value","token_type":"Bearer","expires_in":{{expiresIn}}}"""));
        var middleware = new BasicAuthJwtBridgeMiddleware(_ => Task.CompletedTask, factory, cache,
            NullLogger<BasicAuthJwtBridgeMiddleware>.Instance);

        var context = BuildContextWithBasicAuth("alice@strg.local", "password");
        await middleware.InvokeAsync(context);

        cache.Received(1).Set(
            Arg.Any<string>(),
            Arg.Any<string>(),
            "jwt-value",
            Arg.Is<TimeSpan>(ttl => ttl == TimeSpan.FromSeconds(expiresIn) - BasicAuthJwtBridgeMiddleware.CacheSafetyMargin));
    }

    [Fact]
    public async Task Bearer_authorization_passes_through_unchanged()
    {
        // A WebDAV client that already holds a Bearer token (e.g. the v0.2 OAuth-aware client)
        // must reach StrgWebDavMiddleware with its header untouched. If the bridge mistakenly
        // peeled the Bearer prefix or re-exchanged the token, legitimate Bearer traffic would
        // either 401 or hit the /connect/token path uselessly.
        var cache = Substitute.For<IWebDavJwtCache>();
        var factory = new StubHttpClientFactory(new StubTokenHandler(
            HttpStatusCode.OK, "{}"));
        string? observedHeader = null;
        var middleware = new BasicAuthJwtBridgeMiddleware(
            ctx => { observedHeader = ctx.Request.Headers.Authorization.ToString(); return Task.CompletedTask; },
            factory, cache, NullLogger<BasicAuthJwtBridgeMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer some-jwt-from-an-oauth-aware-client";
        await middleware.InvokeAsync(context);

        observedHeader.Should().Be("Bearer some-jwt-from-an-oauth-aware-client");
        cache.DidNotReceive().TryGet(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Invalid_credentials_yield_401_with_www_authenticate_challenge()
    {
        // WebDAV clients (Windows Explorer, macOS Finder, DAVx5) key off WWW-Authenticate: Basic
        // to re-prompt for credentials. Omitting the header would make a wrong-password case
        // look like "service is broken" rather than "try again" — the user would see no prompt.
        var cache = Substitute.For<IWebDavJwtCache>();
        var factory = new StubHttpClientFactory(new StubTokenHandler(
            HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}"));
        var middleware = new BasicAuthJwtBridgeMiddleware(_ => Task.CompletedTask, factory, cache,
            NullLogger<BasicAuthJwtBridgeMiddleware>.Instance);

        var context = BuildContextWithBasicAuth("alice@strg.local", "wrong-password");
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.Response.Headers[HeaderNames.WWWAuthenticate].ToString()
            .Should().Be("Basic realm=\"strg\"");
    }

    [Fact]
    public async Task Upstream_5xx_yields_502_not_401()
    {
        // RFC 7231 §6.6.3: 502 Bad Gateway for "invalid response from upstream". Crucially,
        // NOT 401 — a 401 would make the WebDAV client re-prompt the user for a password that
        // was never actually wrong. Returning 502 lets the client surface "service unavailable"
        // and avoid credential-thrashing.
        var cache = Substitute.For<IWebDavJwtCache>();
        cache.TryGet(Arg.Any<string>(), Arg.Any<string>()).ReturnsNull();
        var factory = new StubHttpClientFactory(new StubTokenHandler(
            HttpStatusCode.InternalServerError, "boom"));
        var middleware = new BasicAuthJwtBridgeMiddleware(_ => Task.CompletedTask, factory, cache,
            NullLogger<BasicAuthJwtBridgeMiddleware>.Instance);

        var context = BuildContextWithBasicAuth("alice@strg.local", "password");
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        context.Response.Headers[HeaderNames.WWWAuthenticate].Count.Should().Be(0,
            "502 is an upstream-failure signal, not a credential-challenge — WWW-Authenticate would trigger spurious re-prompts");
    }

    [Fact]
    public async Task Cache_hit_rewrites_authorization_and_skips_token_exchange()
    {
        // Proves the cache is actually the hot path: if TryGet returns a cached JWT, the
        // middleware must NOT call the token endpoint. A bug that always re-exchanged would
        // silently burn /connect/token and defeat the 14-minute cache contract.
        var cache = Substitute.For<IWebDavJwtCache>();
        cache.TryGet("alice@strg.local", "password").Returns("cached-jwt");
        var handler = new StubTokenHandler(HttpStatusCode.OK, "{}");
        var factory = new StubHttpClientFactory(handler);
        string? observedHeader = null;
        var middleware = new BasicAuthJwtBridgeMiddleware(
            ctx => { observedHeader = ctx.Request.Headers.Authorization.ToString(); return Task.CompletedTask; },
            factory, cache, NullLogger<BasicAuthJwtBridgeMiddleware>.Instance);

        var context = BuildContextWithBasicAuth("alice@strg.local", "password");
        await middleware.InvokeAsync(context);

        observedHeader.Should().Be("Bearer cached-jwt");
        handler.InvocationCount.Should().Be(0,
            "cache hit must bypass the token endpoint — re-exchange on every request would defeat the 14-minute cache contract");
    }

    [Fact]
    public async Task Cross_tenant_mismatch_on_exchange_yields_401_with_basic_challenge()
    {
        // STRG-073 fold-in #3 — cross-tenant credential-oracle defense. Attacker presents
        // Basic auth creds valid in Tenant B and targets a drive owned by Tenant A. Before the
        // fix, OpenIddict's pre-auth GetByEmailAsync would succeed and issue a JWT for Tenant
        // B's alice; StrgWebDavMiddleware would then resolve the drive under Tenant B, find
        // nothing, and return 404 — leaking to the attacker that their guessed password
        // matched *some* Alice *somewhere*. The bridge must collapse that signal by returning
        // 401 — indistinguishable from a wrong-password failure.
        var driveTenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var jwtTenant = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var driveResolver = Substitute.For<IDriveResolver>();
        driveResolver.GetDriveTenantIdAsync("victim-drive", Arg.Any<CancellationToken>())
            .Returns((Guid?)driveTenant);

        var cache = Substitute.For<IWebDavJwtCache>();
        cache.TryGet(Arg.Any<string>(), Arg.Any<string>()).ReturnsNull();

        var jwt = BuildJwtWithTenantClaim(jwtTenant);
        var factory = new StubHttpClientFactory(new StubTokenHandler(
            HttpStatusCode.OK,
            $$"""{"access_token":"{{jwt}}","token_type":"Bearer","expires_in":900}"""));

        var downstreamCalled = false;
        var middleware = new BasicAuthJwtBridgeMiddleware(
            _ => { downstreamCalled = true; return Task.CompletedTask; },
            factory, cache, NullLogger<BasicAuthJwtBridgeMiddleware>.Instance);

        var context = BuildContextWithBasicAuthAndPath(
            "alice@example.com", "right-pw", "/victim-drive/some/file.txt", driveResolver);
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.Response.Headers[HeaderNames.WWWAuthenticate].ToString()
            .Should().Be("Basic realm=\"strg\"",
                "the challenge must be identical to the wrong-password 401 — leaving the WWW-Authenticate " +
                "header off or using a different scheme would tell the attacker the request reached past " +
                "credential validation, re-opening the oracle");
        downstreamCalled.Should().BeFalse(
            "the request must NOT reach StrgWebDavMiddleware — otherwise the downstream 404 response " +
            "for the wrong-tenant drive would leak the existence bit the bridge is collapsing");
    }

    [Fact]
    public async Task Cross_tenant_mismatch_on_cache_hit_yields_401_without_reexchange()
    {
        // Cache-path mirror of the cross-tenant defense. A cached JWT (issued when the user
        // first hit their own tenant's drive) must NOT be usable as a skeleton key against a
        // different tenant's drive just because (username, password-hash) is the same cache
        // key. A bug that verified tenant only on exchange-miss would silently re-open the
        // oracle the moment the 14-minute cache warmed up.
        var driveTenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var jwtTenant = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var driveResolver = Substitute.For<IDriveResolver>();
        driveResolver.GetDriveTenantIdAsync("victim-drive", Arg.Any<CancellationToken>())
            .Returns((Guid?)driveTenant);

        var jwt = BuildJwtWithTenantClaim(jwtTenant);
        var cache = Substitute.For<IWebDavJwtCache>();
        cache.TryGet("alice@example.com", "right-pw").Returns(jwt);

        var handler = new StubTokenHandler(HttpStatusCode.OK, "{}");
        var factory = new StubHttpClientFactory(handler);

        var downstreamCalled = false;
        var middleware = new BasicAuthJwtBridgeMiddleware(
            _ => { downstreamCalled = true; return Task.CompletedTask; },
            factory, cache, NullLogger<BasicAuthJwtBridgeMiddleware>.Instance);

        var context = BuildContextWithBasicAuthAndPath(
            "alice@example.com", "right-pw", "/victim-drive/foo", driveResolver);
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        handler.InvocationCount.Should().Be(0,
            "cache-hit path must still bypass /connect/token — the tenant verification runs " +
            "against the cached JWT's claims, no re-exchange needed");
        downstreamCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Matching_tenant_claim_rewrites_bearer_and_invokes_downstream()
    {
        // Positive-path pin: when the JWT's tenant_id claim matches the drive's tenant, the
        // bridge rewrites the Authorization header and calls _next. Without this test a bug
        // that 401-ed on every tenant check would pass the mismatch tests above but break
        // every real WebDAV session.
        var sharedTenant = Guid.Parse("44444444-4444-4444-4444-444444444444");

        var driveResolver = Substitute.For<IDriveResolver>();
        driveResolver.GetDriveTenantIdAsync("alice-drive", Arg.Any<CancellationToken>())
            .Returns((Guid?)sharedTenant);

        var jwt = BuildJwtWithTenantClaim(sharedTenant);
        var cache = Substitute.For<IWebDavJwtCache>();
        cache.TryGet(Arg.Any<string>(), Arg.Any<string>()).ReturnsNull();

        var factory = new StubHttpClientFactory(new StubTokenHandler(
            HttpStatusCode.OK,
            $$"""{"access_token":"{{jwt}}","token_type":"Bearer","expires_in":900}"""));

        string? observedHeader = null;
        var middleware = new BasicAuthJwtBridgeMiddleware(
            ctx => { observedHeader = ctx.Request.Headers.Authorization.ToString(); return Task.CompletedTask; },
            factory, cache, NullLogger<BasicAuthJwtBridgeMiddleware>.Instance);

        var context = BuildContextWithBasicAuthAndPath(
            "alice@example.com", "right-pw", "/alice-drive/file.txt", driveResolver);
        await middleware.InvokeAsync(context);

        observedHeader.Should().Be("Bearer " + jwt);
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK,
            "no body written means the test pipeline's default status sticks — the bridge must " +
            "not have short-circuited with 401/502");
    }

    [Fact]
    public async Task Nonexistent_drive_does_not_401_and_passes_through_to_downstream()
    {
        // A URL for a drive that exists nowhere (typo, dangling client bookmark) must fall
        // through to StrgWebDavMiddleware so it can 404 consistently with the "drive missing
        // in your tenant" case. 401ing here would drop a legitimate user into a credential
        // re-prompt loop: Explorer/Finder would re-request the password that was never wrong.
        var driveResolver = Substitute.For<IDriveResolver>();
        driveResolver.GetDriveTenantIdAsync("typo-drive", Arg.Any<CancellationToken>())
            .Returns((Guid?)null);

        var jwt = BuildJwtWithTenantClaim(Guid.Parse("55555555-5555-5555-5555-555555555555"));
        var cache = Substitute.For<IWebDavJwtCache>();
        cache.TryGet(Arg.Any<string>(), Arg.Any<string>()).ReturnsNull();

        var factory = new StubHttpClientFactory(new StubTokenHandler(
            HttpStatusCode.OK,
            $$"""{"access_token":"{{jwt}}","token_type":"Bearer","expires_in":900}"""));

        var downstreamCalled = false;
        var middleware = new BasicAuthJwtBridgeMiddleware(
            _ => { downstreamCalled = true; return Task.CompletedTask; },
            factory, cache, NullLogger<BasicAuthJwtBridgeMiddleware>.Instance);

        var context = BuildContextWithBasicAuthAndPath(
            "alice@example.com", "right-pw", "/typo-drive/foo", driveResolver);
        await middleware.InvokeAsync(context);

        downstreamCalled.Should().BeTrue(
            "no-drive-anywhere must reach StrgWebDavMiddleware so the user sees a 404 — 401 " +
            "would create an auth-prompt loop for a user who just mistyped the drive name");
    }

    [Fact]
    public async Task Malformed_jwt_payload_yields_502_not_401()
    {
        // Defense for a token-endpoint bug: if /connect/token returns 200 with an
        // unparseable access_token, the bridge must NOT fall back to 401 (which would lie
        // about the credentials) and must NOT pass the garbage through to UseAuthentication
        // (which would produce a confusing 500). 502 matches the upstream-failure posture
        // used elsewhere in the bridge — token endpoint produced something we can't use.
        var driveResolver = Substitute.For<IDriveResolver>();
        driveResolver.GetDriveTenantIdAsync("alice-drive", Arg.Any<CancellationToken>())
            .Returns((Guid?)Guid.NewGuid());

        var cache = Substitute.For<IWebDavJwtCache>();
        cache.TryGet(Arg.Any<string>(), Arg.Any<string>()).ReturnsNull();

        var factory = new StubHttpClientFactory(new StubTokenHandler(
            HttpStatusCode.OK,
            """{"access_token":"not-a-jwt-at-all","token_type":"Bearer","expires_in":900}"""));

        var middleware = new BasicAuthJwtBridgeMiddleware(_ => Task.CompletedTask, factory, cache,
            NullLogger<BasicAuthJwtBridgeMiddleware>.Instance);

        var context = BuildContextWithBasicAuthAndPath(
            "alice@example.com", "right-pw", "/alice-drive/foo", driveResolver);
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
    }

    /// <summary>
    /// Builds a non-validated JWT with a <c>tenant_id</c> claim in the payload. The bridge
    /// only peeks at the payload via <see cref="Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler.ReadJsonWebToken"/>
    /// which does not verify the signature, so the opaque signature segment here is sufficient.
    /// </summary>
    private static string BuildJwtWithTenantClaim(Guid tenantId)
    {
        static string Base64Url(ReadOnlySpan<byte> bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var payload = Base64Url(Encoding.UTF8.GetBytes(
            $$"""{"sub":"alice","tenant_id":"{{tenantId}}"}"""));
        var signature = Base64Url([0x01, 0x02, 0x03]);
        return $"{header}.{payload}.{signature}";
    }

    private static DefaultHttpContext BuildContextWithBasicAuthAndPath(
        string username, string password, string path, IDriveResolver driveResolver)
    {
        var context = BuildContextWithBasicAuth(username, password);
        context.Request.Path = path;
        var services = new ServiceCollection();
        services.AddSingleton(driveResolver);
        context.RequestServices = services.BuildServiceProvider();
        return context;
    }

    private static DefaultHttpContext BuildContextWithBasicAuth(string username, string password)
    {
        var context = new DefaultHttpContext();
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        context.Request.Headers.Authorization = $"Basic {encoded}";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }

    // HttpMessageHandler stub that returns a fixed (status, body) for POST /connect/token and
    // tracks invocation count so the cache-hit test can assert zero re-exchanges.
    private sealed class StubTokenHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public int InvocationCount { get; private set; }

        public StubTokenHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
