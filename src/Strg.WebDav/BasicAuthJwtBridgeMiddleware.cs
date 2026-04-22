using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Strg.WebDav;

/// <summary>
/// STRG-073 — intercepts HTTP Basic Auth on the <c>/dav</c> branch and rewrites the
/// Authorization header to a Bearer JWT so downstream middleware (UseAuthentication →
/// <see cref="StrgWebDavMiddleware"/>) can run the normal OIDC validation path.
///
/// <para><b>Why a bridge.</b> WebDAV clients in the real world — Windows Explorer, macOS Finder,
/// DAVx5 on Android, mobile WebDAV apps — only speak <c>Basic user:password</c>. strg's auth
/// stack past <c>/connect/token</c> only accepts Bearer JWTs. Without this bridge, every
/// WebDAV-compatible OS client would get a 401 and the whole surface would be unreachable.</para>
///
/// <para><b>Where it runs.</b> Registered <i>inside</i> the <c>app.Map("/dav", …)</c> branch,
/// BEFORE the branch's <c>UseAuthentication()</c> call — see <c>Program.cs</c>. Placing it
/// outside the branch would expose the password-grant exchange to GraphQL, REST, and token
/// endpoints that have no legitimate need for credential re-submission, and would also leak
/// cached Bearer tokens to non-WebDAV traffic. The outer pipeline's <c>UseAuthentication</c>
/// sees the Basic header as "no valid scheme" and leaves <c>HttpContext.User</c> anonymous; we
/// rewrite the header, then the branch's <c>UseAuthentication</c> re-runs the OIDC validator
/// against the fresh Bearer token.</para>
///
/// <para><b>Failure mapping.</b>
/// <list type="bullet">
///   <item><description><b>401</b> + <c>WWW-Authenticate: Basic realm="strg"</c> — malformed
///     Basic header (non-base64, missing colon, empty credentials) OR /connect/token returned a
///     4xx. The <c>WWW-Authenticate</c> challenge is what WebDAV clients key off to re-prompt
///     the user; omitting it turns a credential-change bug into a stuck-and-silent
///     failure.</description></item>
///   <item><description><b>502 Bad Gateway</b> — /connect/token returned 5xx or the HTTP call
///     itself failed (timeout, network). The bridge cannot determine whether the credentials
///     are valid; fail-closed but distinguish from 401 so clients don't re-prompt for a
///     password that wasn't actually wrong. RFC 7231 §6.6.3 specifies 502 for "invalid
///     response from upstream"; the token endpoint is our upstream from the bridge's
///     perspective even though it shares a process.</description></item>
/// </list>
/// No-Authorization-header requests pass through unchanged — OPTIONS capability probes are
/// anonymous per RFC 4918 §10.1, and <see cref="StrgWebDavMiddleware"/> enforces auth
/// explicitly for other verbs.</para>
///
/// <para><b>Password-handling discipline.</b> The plain password enters three surfaces and no
/// more: (1) the decoded <c>credentials</c> local, (2) the form-urlencoded body sent to
/// <c>/connect/token</c>, (3) the SHA-256 digest used as the cache key suffix. It is never
/// logged, never stored, never returned. <c>SecretFieldsDestructuringPolicy</c> (STRG-006) is
/// unrelated here — the bridge logs only the username + the failure mode, and Serilog's
/// destructurer never sees the password object at all because the local is never passed to
/// <c>_logger.Log(…)</c>.</para>
/// </summary>
public sealed class BasicAuthJwtBridgeMiddleware
{
    private const string BearerPrefix = "Bearer ";
    private const string BasicPrefix = "Basic ";
    private const string WwwAuthenticateChallenge = "Basic realm=\"strg\"";
    private const string TokenEndpoint = "/connect/token";

    /// <summary>
    /// Named <see cref="IHttpClientFactory"/> key consumed by the bridge when posting to
    /// <c>/connect/token</c>. Exposed <c>internal</c> so <see cref="WebDavServiceExtensions"/>
    /// binds the <see cref="HttpClient.BaseAddress"/> against the same key — a mismatch between
    /// registration and lookup would silently hand the bridge an unconfigured default client and
    /// blow up at request time with a <c>BaseAddress == null</c> NRE instead of a config error.
    /// </summary>
    internal const string OidcHttpClientName = "oidc";
    private const string WebDavClientId = "webdav-internal";

    /// <summary>
    /// Scope bundle the bridge requests from the token endpoint. Matches the permission set
    /// granted to <c>webdav-internal</c> in <c>OpenIddictSeedWorker.BuildWebDavInternalDescriptor</c>.
    /// A narrower set here would force clients to use less-privileged tokens; a wider set would
    /// be rejected by OpenIddict as an unauthorized-scope grant.
    /// </summary>
    private const string RequestedScopes = "files.read files.write tags.write";

    /// <summary>
    /// Safety margin subtracted from the server-issued <c>expires_in</c> before setting the
    /// cache TTL. 60s is the smallest interval that survives both clock skew between the
    /// server's token issuance and the cache's first hit AND the worst-case WebDAV-client
    /// round-trip time on a slow connection. Too small: clients receive a just-expired token
    /// on cache hit. Too large: cache churn reduces hit-rate without meaningful safety gain.
    /// </summary>
    internal static readonly TimeSpan CacheSafetyMargin = TimeSpan.FromSeconds(60);

    private readonly RequestDelegate _next;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWebDavJwtCache _cache;
    private readonly ILogger<BasicAuthJwtBridgeMiddleware> _logger;

    public BasicAuthJwtBridgeMiddleware(
        RequestDelegate next,
        IHttpClientFactory httpClientFactory,
        IWebDavJwtCache cache,
        ILogger<BasicAuthJwtBridgeMiddleware> logger)
    {
        _next = next;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var authHeader = context.Request.Headers.Authorization.ToString();

        // Non-Basic auth (Bearer, or nothing at all) flows through unchanged. Bearer tokens go
        // straight to OIDC validation in the branch's UseAuthentication; anonymous requests
        // reach StrgWebDavMiddleware, which lets OPTIONS through and 401s everything else.
        if (!authHeader.StartsWith(BasicPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!TryParseBasicAuth(authHeader, out var username, out var password))
        {
            await RespondUnauthorizedAsync(context);
            return;
        }

        var cached = _cache.TryGet(username, password);
        // IsNullOrEmpty rather than "is not null": the cache contract returns null on miss, but a
        // future test double or a corrupt in-memory entry could surface an empty string. Treating
        // empty as miss is defense-in-depth — rewriting the Authorization header to literal
        // "Bearer " (no token) would hand StrgWebDavMiddleware a technically-well-formed header
        // that OIDC would reject as malformed, producing a confusing 500 instead of re-exchanging.
        if (!string.IsNullOrEmpty(cached))
        {
            context.Request.Headers.Authorization = BearerPrefix + cached;
            await _next(context);
            return;
        }

        var exchange = await ExchangeForJwtAsync(username, password, context.RequestAborted);
        if (exchange.Outcome == ExchangeOutcome.InvalidCredentials)
        {
            _logger.LogInformation(
                "WebDAV Basic Auth exchange rejected by OpenIddict for user {Username}", username);
            await RespondUnauthorizedAsync(context);
            return;
        }
        if (exchange.Outcome == ExchangeOutcome.UpstreamFailure)
        {
            _logger.LogWarning(
                "WebDAV Basic Auth exchange failed upstream at {TokenEndpoint} for user {Username} — returning 502",
                TokenEndpoint, username);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        _cache.Set(username, password, exchange.AccessToken!, exchange.CacheTtl);
        context.Request.Headers.Authorization = BearerPrefix + exchange.AccessToken;
        await _next(context);
    }

    /// <summary>
    /// Parses <c>Basic &lt;base64(user:pass)&gt;</c>. Returns <c>false</c> on any shape the
    /// grammar doesn't permit: non-base64 payload, missing colon, empty username, empty
    /// password. Each of these is equivalent to invalid credentials from the caller's
    /// perspective — there's no useful distinction between "malformed" and "wrong password"
    /// on the response surface (both 401 the same way).
    /// </summary>
    private static bool TryParseBasicAuth(
        string authHeader,
        out string username,
        out string password)
    {
        username = string.Empty;
        password = string.Empty;

        var payload = authHeader[BasicPrefix.Length..].Trim();
        if (payload.Length == 0)
        {
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return false;
        }

        var text = Encoding.UTF8.GetString(decoded);
        var colonIndex = text.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex <= 0 || colonIndex == text.Length - 1)
        {
            return false;
        }

        username = text[..colonIndex];
        password = text[(colonIndex + 1)..];
        return true;
    }

    private async Task<ExchangeResult> ExchangeForJwtAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(OidcHttpClientName);
        var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "password"),
            new KeyValuePair<string, string>("username", username),
            new KeyValuePair<string, string>("password", password),
            new KeyValuePair<string, string>("scope", RequestedScopes),
            new KeyValuePair<string, string>("client_id", WebDavClientId),
        ]);

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(TokenEndpoint, form, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "WebDAV Basic Auth exchange: HTTP request to {TokenEndpoint} threw — returning upstream failure",
                TokenEndpoint);
            return ExchangeResult.UpstreamFailure();
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // TaskCanceledException without the caller's cancellation token means the HttpClient's
            // own timeout fired. Treat as upstream failure — the token endpoint is unreachable
            // within the budget, not that the caller gave up.
            _logger.LogWarning(ex,
                "WebDAV Basic Auth exchange: {TokenEndpoint} timed out — returning upstream failure",
                TokenEndpoint);
            return ExchangeResult.UpstreamFailure();
        }

        using (response)
        {
            if ((int)response.StatusCode >= 500)
            {
                return ExchangeResult.UpstreamFailure();
            }
            if (!response.IsSuccessStatusCode)
            {
                return ExchangeResult.InvalidCredentials();
            }

            TokenResponsePayload? payload;
            try
            {
                payload = await response.Content.ReadFromJsonAsync<TokenResponsePayload>(cancellationToken);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "WebDAV Basic Auth exchange: {TokenEndpoint} returned 2xx with non-JSON body — returning upstream failure",
                    TokenEndpoint);
                return ExchangeResult.UpstreamFailure();
            }

            if (payload is null || string.IsNullOrEmpty(payload.AccessToken))
            {
                return ExchangeResult.UpstreamFailure();
            }

            var lifetime = payload.ExpiresIn > 0
                ? TimeSpan.FromSeconds(payload.ExpiresIn)
                : TimeSpan.FromMinutes(15);
            var ttl = lifetime - CacheSafetyMargin;
            return ExchangeResult.Success(payload.AccessToken, ttl);
        }
    }

    private static async Task RespondUnauthorizedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers[HeaderNames.WWWAuthenticate] = WwwAuthenticateChallenge;
        await context.Response.CompleteAsync();
    }

    private enum ExchangeOutcome
    {
        Success,
        InvalidCredentials,
        UpstreamFailure,
    }

    private readonly struct ExchangeResult
    {
        public ExchangeOutcome Outcome { get; }
        public string? AccessToken { get; }
        public TimeSpan CacheTtl { get; }

        private ExchangeResult(ExchangeOutcome outcome, string? accessToken, TimeSpan cacheTtl)
        {
            Outcome = outcome;
            AccessToken = accessToken;
            CacheTtl = cacheTtl;
        }

        public static ExchangeResult Success(string accessToken, TimeSpan ttl) =>
            new(ExchangeOutcome.Success, accessToken, ttl);
        public static ExchangeResult InvalidCredentials() =>
            new(ExchangeOutcome.InvalidCredentials, null, TimeSpan.Zero);
        public static ExchangeResult UpstreamFailure() =>
            new(ExchangeOutcome.UpstreamFailure, null, TimeSpan.Zero);
    }

    // Init-only-properties class rather than positional record: on positional records,
    // [property: JsonPropertyName] binds to the property metadata only, and STJ's ctor-based
    // deserialization path picks up the untagged parameter name (PascalCase "AccessToken") and
    // fails to match "access_token". Dropping to an init-only class puts the attribute on the
    // property STJ actually reads.
    private sealed class TokenResponsePayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }
}
