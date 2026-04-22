using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.Net.Http.Headers;
using Strg.Core.Constants;

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
///
/// <para><b>Cross-tenant credential-oracle defense (fold-in #3).</b> Strg permits the same email
/// address to exist in multiple tenants (the uniqueness constraint is on
/// <c>(tenant_id, email)</c>, not email alone, until the v0.2 decision lands). Without
/// defensive action the bridge would leak a cross-tenant password oracle:
/// <list type="number">
///   <item><description>Attacker sends <c>Basic alice@example.com:p</c> to
///     <c>/dav/victim-tenant-drive/</c>.</description></item>
///   <item><description><c>/connect/token</c> runs <c>GetByEmailAsync</c> which is unfiltered
///     pre-auth (CLAUDE.md carve-out); it finds SOME alice and issues a JWT for that alice's
///     tenant.</description></item>
///   <item><description>If <c>p</c> happens to match any alice's password anywhere, the
///     exchange succeeds and <see cref="StrgWebDavMiddleware"/>'s tenant-scoped resolver
///     returns <c>null</c> for the victim drive → 404. If <c>p</c> matches no alice, the
///     exchange fails → 401.</description></item>
///   <item><description>The 404-vs-401 distinction tells the attacker whether their guessed
///     password matches ANY alice in ANY tenant — a cross-tenant credential oracle.</description></item>
/// </list>
/// The fix is to verify <b>before</b> passing the request through that the JWT's
/// <c>tenant_id</c> claim equals the tenant that actually owns the drive at
/// <c>/dav/{driveName}/</c>. On mismatch the bridge returns 401 with the same WWW-Authenticate
/// challenge as a wrong-password failure, collapsing the oracle: attacker sees only "the
/// credential you presented won't open this URL" without learning why. The drive's tenant is
/// fetched via <see cref="IDriveResolver.GetDriveTenantIdAsync"/>, which bypasses the tenant
/// filter (CLAUDE.md carve-out — this executes before the branch's UseAuthentication has
/// populated <c>ITenantContext</c>). The JWT's tenant claim is read by peeking at the payload
/// via <see cref="JsonWebTokenHandler.ReadJsonWebToken"/> — signature validation is explicitly
/// skipped because the branch's <c>UseAuthentication</c> downstream performs the full
/// validation, and a double-validation here would double the crypto cost on every request.</para>
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

    // STRG-073 fold-in #1 — the requested-scope string is intentionally NOT a const here.
    // Both this middleware and OpenIddictSeedWorker.BuildWebDavInternalDescriptor read from
    // Strg.Core.Constants.WebDavScopes so a scope addition/removal lands in one place. See
    // WebDavScopes.cs for the rationale on why these three and no more.

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

    // Thread-safe per Microsoft's API contract; reused so every request doesn't pay the handler-
    // construction cost. The handler is used purely for the UNVALIDATED ReadJsonWebToken peek —
    // signature validation happens downstream in the branch's UseAuthentication.
    private static readonly JsonWebTokenHandler JwtPayloadPeeker = new();

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

        // Drive name is extracted from the request path EARLY because it's the anchor for the
        // cross-tenant verification step, and extraction is cheap + deterministic. Pulling it
        // once up here avoids duplicating the span-walk on the cache-hit vs exchange branches.
        // The /dav prefix is already stripped by app.Map, so the path starts with /{driveName}.
        var driveName = ExtractDriveName(context.Request.Path);

        string accessToken;
        var cached = _cache.TryGet(username, password);
        // IsNullOrEmpty rather than "is not null": the cache contract returns null on miss, but a
        // future test double or a corrupt in-memory entry could surface an empty string. Treating
        // empty as miss is defense-in-depth — rewriting the Authorization header to literal
        // "Bearer " (no token) would hand StrgWebDavMiddleware a technically-well-formed header
        // that OIDC would reject as malformed, producing a confusing 500 instead of re-exchanging.
        if (!string.IsNullOrEmpty(cached))
        {
            accessToken = cached;
        }
        else
        {
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

            accessToken = exchange.AccessToken!;
            _cache.Set(username, password, accessToken, exchange.CacheTtl);
        }

        // Cross-tenant verification MUST run on both cache-hit and post-exchange paths: a cached
        // JWT for user@tenant-X could otherwise be used against /dav/tenant-y-drive/ as long as
        // the cache key (username + password hash) matched, silently opening the oracle we
        // collapsed on the exchange path. See the xmldoc block on this class for the full attack.
        if (driveName is not null)
        {
            var verification = await VerifyTenantMatchAsync(
                context, accessToken, driveName, username, context.RequestAborted);
            if (verification == TenantVerificationOutcome.Mismatch)
            {
                // Evict the colliding cache entry so the next retry re-exchanges against
                // /connect/token with fresh tenant-resolution. The cache key today is
                // webdav-jwt:{username}:{HEX(SHA256(password))} — it carries no tenant component,
                // so alice@tenant-A and alice@tenant-B collide on the same slot. Without this
                // surgical eviction, whichever tenant lost the race is stuck in a 401 loop for
                // the full ≤14-min JWT TTL (self-DoS by cache-key collision). Single-entry Remove
                // is deliberate over InvalidateUser(username): sibling entries for the same email
                // under a DIFFERENT password hash (e.g. the OTHER tenant's legitimate session)
                // must survive. A tenant-scoped cache key (task #146) is the structural fix; this
                // is the narrow availability win that ships first.
                _cache.Remove(username, password);

                // RespondUnauthorizedAsync — SAME shape as a credential rejection. Returning 403
                // would tell the attacker "credential was accepted, but not for this tenant";
                // returning 404 would tell them "drive doesn't exist in your tenant (so it exists
                // elsewhere)". 401 collapses both signals.
                await RespondUnauthorizedAsync(context);
                return;
            }
            if (verification == TenantVerificationOutcome.MalformedJwt)
            {
                // Should never happen in production — a JWT we just received from /connect/token
                // (or a cache entry the same endpoint issued) must have the tenant_id claim or
                // the whole token-shape contract is broken. 502 because the problem originates
                // upstream, not in the caller's credentials.
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }
            // Match OR no-drive-exists — fall through. The "no drive anywhere" case lets the
            // request reach StrgWebDavMiddleware, which 404s consistently with how it handles a
            // drive truly missing in the caller's own tenant; we deliberately do not 401 here so
            // a legitimate user who typos a drive name doesn't land in a re-auth loop.
        }

        context.Request.Headers.Authorization = BearerPrefix + accessToken;
        await _next(context);
    }

    /// <summary>
    /// Peeks at the JWT's <c>tenant_id</c> claim and compares it to the drive's tenant (looked
    /// up via <see cref="IDriveResolver.GetDriveTenantIdAsync"/>). Returns
    /// <see cref="TenantVerificationOutcome.Match"/> on hit, <see cref="TenantVerificationOutcome.Mismatch"/>
    /// when the claims point to different tenants, <see cref="TenantVerificationOutcome.NoSuchDrive"/>
    /// when the drive name doesn't exist in any tenant (the request will 404 downstream),
    /// and <see cref="TenantVerificationOutcome.MalformedJwt"/> when the token can't be parsed
    /// or lacks the <c>tenant_id</c> claim.
    /// </summary>
    private async Task<TenantVerificationOutcome> VerifyTenantMatchAsync(
        HttpContext context,
        string accessToken,
        string driveName,
        string username,
        CancellationToken cancellationToken)
    {
        if (!TryPeekJwtTenantId(accessToken, out var jwtTenantId))
        {
            _logger.LogWarning(
                "WebDAV Basic Auth: JWT for user {Username} is missing or has unparseable tenant_id claim",
                username);
            return TenantVerificationOutcome.MalformedJwt;
        }

        // Middleware is singleton but IDriveResolver is scoped (owns a per-request StrgDbContext);
        // RequestServices is the per-request scope, and GetRequiredService gives us an instance
        // tied to that scope's lifetime.
        var resolver = context.RequestServices.GetRequiredService<IDriveResolver>();
        var driveTenantId = await resolver.GetDriveTenantIdAsync(driveName, cancellationToken);
        if (driveTenantId is null)
        {
            return TenantVerificationOutcome.NoSuchDrive;
        }
        if (driveTenantId.Value != jwtTenantId)
        {
            _logger.LogWarning(
                "WebDAV Basic Auth: cross-tenant mismatch for user {Username} on drive {DriveName} — JWT tenant does not match drive tenant; returning 401",
                username, driveName);
            return TenantVerificationOutcome.Mismatch;
        }
        return TenantVerificationOutcome.Match;
    }

    /// <summary>
    /// Base64url-decodes the JWT payload (no signature validation — that's the downstream
    /// <c>UseAuthentication</c>'s job) and extracts the <c>tenant_id</c> claim as a
    /// <see cref="Guid"/>. Returns <c>false</c> on any shape that prevents extraction:
    /// malformed token, missing claim, non-Guid value.
    /// </summary>
    private static bool TryPeekJwtTenantId(string accessToken, out Guid tenantId)
    {
        tenantId = Guid.Empty;
        JsonWebToken parsed;
        try
        {
            parsed = JwtPayloadPeeker.ReadJsonWebToken(accessToken);
        }
        catch (Exception)
        {
            // JsonWebTokenHandler throws a handful of distinct types (SecurityTokenMalformedException,
            // ArgumentException on base64 failures, etc.). We do not branch on them — any parse
            // failure maps to "can't peek" from the bridge's perspective.
            return false;
        }

        if (!parsed.TryGetPayloadValue<string>(StrgClaimNames.TenantId, out var raw)
            || string.IsNullOrEmpty(raw))
        {
            return false;
        }
        return Guid.TryParse(raw, out tenantId);
    }

    /// <summary>
    /// app.Map("/dav", ...) strips the prefix before this middleware runs, so
    /// <see cref="HttpRequest.Path"/> at bridge time starts with <c>/{driveName}[/remainder]</c>.
    /// This mirrors <see cref="StrgWebDavMiddleware"/>'s own extractor; duplicating the ~10 lines
    /// is cheaper than carving out a shared utility that both singletons would have to
    /// cross-reference. Returns <c>null</c> for an empty path (the branch root <c>/dav/</c>,
    /// which reaches us as <c>/</c>) — no drive means no tenant verification.
    /// </summary>
    private static string? ExtractDriveName(PathString path)
    {
        if (!path.HasValue)
        {
            return null;
        }

        var value = path.Value!.AsSpan();
        if (value.Length == 0 || value[0] != '/')
        {
            return null;
        }

        value = value[1..];
        var slashIndex = value.IndexOf('/');
        var segment = slashIndex < 0 ? value : value[..slashIndex];
        return segment.IsEmpty ? null : segment.ToString();
    }

    private enum TenantVerificationOutcome
    {
        Match,
        Mismatch,
        NoSuchDrive,
        MalformedJwt,
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
            new KeyValuePair<string, string>("scope", WebDavScopes.SpaceSeparated),
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
