---
id: STRG-073
title: Implement WebDAV Basic Auth → JWT bridge
milestone: v0.1
priority: high
status: open
type: implementation
labels: [webdav, auth, security]
depends_on: [STRG-067, STRG-015]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-073: Implement WebDAV Basic Auth to JWT bridge

## Summary

WebDAV clients (Windows Explorer, macOS Finder, DAVx5) use HTTP Basic Auth (`Authorization: Basic base64(user:password)`). strg's API requires JWT Bearer tokens. Implement a middleware that intercepts Basic Auth, exchanges credentials for a JWT via the OpenIddict token endpoint, and forwards the request with a Bearer token.

## Technical Specification

### File: `src/Strg.WebDav/BasicAuthJwtBridgeMiddleware.cs`

```csharp
public sealed class BasicAuthJwtBridgeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BasicAuthJwtBridgeMiddleware> _logger;

    public async Task InvokeAsync(HttpContext context)
    {
        var authHeader = context.Request.Headers.Authorization.ToString();

        if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            var credentials = ParseBasicAuth(authHeader);
            if (credentials is var (username, password))
            {
                var jwt = await ExchangeForJwtAsync(username, password, context.RequestAborted);
                if (jwt != null)
                {
                    // Replace Authorization header with Bearer token
                    context.Request.Headers.Authorization = $"Bearer {jwt}";
                }
                else
                {
                    context.Response.StatusCode = 401;
                    context.Response.Headers.WWWAuthenticate = "Basic realm=\"strg\"";
                    return;
                }
            }
        }

        await _next(context);
    }

    private async Task<string?> ExchangeForJwtAsync(string username, string password, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("oidc");
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "password"),
            new KeyValuePair<string, string>("username", username),
            new KeyValuePair<string, string>("password", password),
            new KeyValuePair<string, string>("scope", "files.read files.write tags.write"),
            new KeyValuePair<string, string>("client_id", "webdav-internal"),
        }), ct);

        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.GetProperty("access_token").GetString();
    }
}
```

### Token caching:

Exchanged tokens are cached per `(username, SHA256(password))` key using `IMemoryCache` with TTL = 15 minutes minus 60 seconds (matching the JWT access token lifetime). This avoids a token request on every WebDAV method call.

**Cache invalidation on password change:** When a user changes their password (via `changePassword` GraphQL mutation), the cache must be explicitly flushed for that user. `IWebDavJwtCache.InvalidateUser(string username)` is injected into the `changePassword` handler.

```csharp
var cacheKey = $"webdav-jwt:{username}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)))}";
```

### Registration (inside `/dav/{driveName}` map):

```csharp
webdavApp.UseMiddleware<BasicAuthJwtBridgeMiddleware>();
webdavApp.UseAuthentication();
webdavApp.UseAuthorization();
webdavApp.UseMiddleware<StrgWebDavMiddleware>();
```

### OpenIddict internal client:

Register a dedicated `webdav-internal` client application in OpenIddict (no secret, password grant allowed only for WebDAV route).

## Acceptance Criteria

- [ ] `PROPFIND /dav/my-drive/` with `Authorization: Basic base64(user:pass)` → `207 Multi-Status`
- [ ] Invalid credentials → `401 Unauthorized` with `WWW-Authenticate: Basic realm="strg"` header
- [ ] Valid token cached — second request uses cached token (no second call to `/connect/token`)
- [ ] Cache TTL = 14 minutes (access token is 15min, cache expires 60s early to avoid serving expired token)
- [ ] Password change (`changePassword` mutation) calls `IWebDavJwtCache.InvalidateUser(username)` — stale cache evicted immediately
- [ ] Password is never stored in cache (only hash used as cache key)
- [ ] Bridge only active on `/dav/` routes (not on REST or GraphQL)

## Test Cases

- **TC-001**: Basic Auth with valid credentials → WebDAV request succeeds
- **TC-002**: Basic Auth with wrong password → 401
- **TC-003**: Second request → token endpoint NOT called (cache hit)
- **TC-004**: Token expires → cache evicted → new token fetched on next request
- **TC-005**: Bearer Auth on WebDAV route → bridge skips, passes through unchanged

## Implementation Tasks

- [ ] Create `BasicAuthJwtBridgeMiddleware.cs` in `Strg.WebDav/`
- [ ] Register `webdav-internal` client in OpenIddict setup (STRG-012)
- [ ] Configure `IMemoryCache` token cache
- [ ] Register `IHttpClientFactory` with `oidc` named client pointing to `/connect/token`
- [ ] Register middleware in `/dav/` map only

## Testing Tasks

- [ ] Integration test: Basic Auth request → Bearer forwarded to next middleware
- [ ] Integration test: bad password → 401
- [ ] Unit test: cache key never contains plain password
- [ ] Unit test: cache TTL is `token_expires_at - 60s`

## Security Review Checklist

- [ ] Plain password never logged or cached
- [ ] Cache key uses SHA-256 hash of password (not plain text)
- [ ] Timing-safe comparison for credential validation (handled by OpenIddict, not here)
- [ ] `WWW-Authenticate` header present on 401 (required for WebDAV client retry)
- [ ] Bridge only active on `/dav/` routes

## Code Review Checklist

- [ ] `IHttpClientFactory` used (not `new HttpClient()`)
- [ ] Named client `oidc` configured with correct base URL
- [ ] Cache entry is `string` (JWT only), not the full response

## Definition of Done

- [ ] Windows Explorer WebDAV mount authenticates with username/password
- [ ] No token call per request (cache verified by log inspection)
