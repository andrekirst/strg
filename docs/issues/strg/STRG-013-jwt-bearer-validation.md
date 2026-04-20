---
id: STRG-013
title: Configure JWT Bearer token validation middleware
milestone: v0.1
priority: critical
status: open
type: implementation
labels: [security, auth, middleware]
depends_on: [STRG-012]
blocks: [STRG-015, STRG-034, STRG-050]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-013: Configure JWT Bearer token validation middleware

## Summary

Configure ASP.NET Core authentication middleware to validate JWT Bearer tokens issued by the embedded OpenIddict server. Every protected API endpoint and GraphQL operation must require a valid token.

## Technical Specification

OpenIddict's validation middleware handles this natively — it validates tokens against the local server without network calls:

```csharp
// Already added by OpenIddict validation (STRG-012):
// .AddValidation(o => { o.UseLocalServer(); o.UseAspNetCore(); })

// Authorization policies:
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("FilesRead", p => p.RequireScope("files.read"));
    options.AddPolicy("FilesWrite", p => p.RequireScope("files.write"));
    options.AddPolicy("Admin", p => p.RequireScope("admin")); // JWT scope only — not User.Role
    options.AddPolicy("Authenticated", p => p.RequireAuthenticatedUser());
    options.FallbackPolicy = options.DefaultPolicy; // require auth everywhere
});
```

### Scope claims extension:

```csharp
public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(Claims.Subject) ?? throw new InvalidOperationException("No sub claim"));

    public static Guid GetTenantId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue("tenant_id") ?? throw new InvalidOperationException("No tenant_id claim"));

    public static bool HasScope(this ClaimsPrincipal user, string scope) =>
        user.FindAll("scope").Any(c => c.Value.Split(' ').Contains(scope));
}
```

## Acceptance Criteria

- [ ] All API endpoints require a valid JWT by default (fallback policy)
- [ ] Health check endpoints (`/health/*`) exempt from auth
- [ ] Prometheus metrics endpoint (`/metrics`) exempt from auth
- [ ] OpenAPI endpoints exempt from auth
- [ ] OIDC endpoints (`/connect/*`, `/.well-known/*`) exempt from auth
- [ ] `[AllowAnonymous]` can be used to explicitly exempt endpoints
- [ ] Policy `FilesRead` requires `files.read` scope in token
- [ ] Policy `Admin` requires `admin` scope in JWT (not a role claim) — `User.Role` determines whether `admin` scope is issued at login, but is not rechecked per-request
- [ ] `ClaimsPrincipalExtensions.GetUserId()` works on all authenticated requests
- [ ] Expired token → 401 (not 403)
- [ ] Valid token, wrong scope → 403 (not 401)

## Test Cases

- **TC-001**: No Authorization header → 401
- **TC-002**: Malformed token → 401
- **TC-003**: Expired token → 401
- **TC-004**: Valid token, missing required scope → 403
- **TC-005**: Valid token, correct scope → 200
- **TC-006**: GET `/health/ready` with no token → 200 (exempt)
- **TC-007**: GET `/metrics` with no token → 200 (exempt)

## Implementation Tasks

- [ ] Configure authorization policies in `Program.cs`
- [ ] Set fallback policy to require authenticated user
- [ ] Exempt health, metrics, OIDC, and OpenAPI endpoints
- [ ] Create `ClaimsPrincipalExtensions.cs` in `Strg.Core` or `Strg.Api`
- [ ] Create `RequireScopeAttribute` for endpoint-level scope enforcement
- [ ] Write integration tests covering all TC scenarios

## Security Review Checklist

- [ ] Fallback policy is set — no endpoints are accidentally unprotected
- [ ] Scope validation is done per-claim, not per-string-match on the full scope string
- [ ] Token signature validation is not disabled anywhere
- [ ] `GetUserId()` throws (not returns default Guid) when claim is missing

## Code Review Checklist

- [ ] Policy names are constants, not inline strings
- [ ] `ClaimsPrincipalExtensions` uses `FindFirstValue` not `FindFirst().Value` (null safety)
- [ ] Exempt endpoints are listed and documented

## Definition of Done

- [ ] All test cases pass
- [ ] No unprotected endpoints (verified by reviewing all `app.Map*` calls)
