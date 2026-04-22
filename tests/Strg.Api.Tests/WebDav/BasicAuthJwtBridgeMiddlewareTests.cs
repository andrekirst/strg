using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
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
