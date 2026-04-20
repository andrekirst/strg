---
id: STRG-082
title: Implement ASP.NET Core rate limiting middleware
milestone: v0.1
priority: high
status: open
type: implementation
labels: [security, api, infrastructure]
depends_on: [STRG-010]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-082: Implement ASP.NET Core rate limiting middleware

## Summary

Configure ASP.NET Core's built-in rate limiting middleware (`Microsoft.AspNetCore.RateLimiting`) with multiple policies: a global API limit, a stricter auth endpoint limit, and a TUS upload chunk limit. In v0.1, counters are stored in-memory (per-process). In v0.2, Redis is used for multi-instance distribution.

## Technical Specification

### Registration in `Program.cs`:

```csharp
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.Headers["Retry-After"] = "60";
        await ctx.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Rate limit exceeded. Try again in 60 seconds." }, ct);
    };

    // Global: 300 req/min per IP
    options.AddPolicy("global", ctx => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 300,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        }));

    // Auth endpoints: 10 req/min per IP (brute-force protection — /connect/token only)
    // NOTE: /api/v1/users/register is NOT rate limited (public self-registration)
    options.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        }));

    // TUS upload: 100 chunks/min per user (high volume expected)
    options.AddPolicy("upload", ctx =>
    {
        var userId = ctx.HttpContext.User.FindFirst("sub")?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: userId,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1)
            });
    });
});
```

### Middleware registration (in `Program.cs` before routes):

```csharp
app.UseRateLimiter();
```

### Policy assignment to routes:

```csharp
// Auth endpoints
app.MapPost("/connect/token", ...).RequireRateLimiting("auth");
// /api/v1/users/register has NO rate limiting (public self-registration, quota is the guard)

// TUS upload is EXCLUDED from rate limiting (large file uploads, not brute-force target)
// No .RequireRateLimiting() on TUS endpoint

// All other routes get global policy via default
options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1) }));
```

### Rate limit policy configuration (from `appsettings.json`):

```json
{
  "RateLimiting": {
    "Global": { "PermitLimit": 300, "WindowSeconds": 60 },
    "Auth": { "PermitLimit": 10, "WindowSeconds": 60 },
    "Upload": { "PermitLimit": 100, "WindowSeconds": 60 }
  }
}
```

## Acceptance Criteria

- [ ] Sending 11 requests to `/connect/token` in 1 minute → 11th returns `429`
- [ ] Sending 301 requests to any API endpoint → 301st returns `429`
- [ ] `429` response includes `Retry-After: 60` header
- [ ] Rate limiting keyed by IP (unauthenticated) or user ID (authenticated)
- [ ] `/health/live` exempt from rate limiting
- [ ] Rate limit policy values configurable via `appsettings.json`

## Test Cases

- **TC-001**: 10 auth requests → success; 11th → `429` with `Retry-After`
- **TC-002**: 300 API requests in 1 min → success; 301st → `429`
- **TC-003**: `/health/live` not rate limited (after 1000 requests)
- **TC-004**: `appsettings.json` limits respected

## Implementation Tasks

- [ ] Add `AddRateLimiter` configuration in `Program.cs`
- [ ] Bind rate limit options from `appsettings.json` to typed options class
- [ ] Apply `auth` policy to token endpoint and registration
- [ ] Apply `upload` policy to TUS endpoint
- [ ] Exempt health check endpoints from rate limiting
- [ ] Add `TODO: v0.2` comment for Redis-backed distributed counters

## Testing Tasks

- [ ] Integration test: 11 token requests in burst → 11th gets 429
- [ ] Integration test: health endpoint not rate limited

## Security Review Checklist

- [ ] Auth endpoints have strictest limit (10/min)
- [ ] IP extraction handles proxy headers (`X-Forwarded-For`) — use `IHttpContextAccessor` with `UseForwardedHeaders` in production
- [ ] `Retry-After` header present on all 429 responses

## Code Review Checklist

- [ ] Limits are configurable (not hardcoded)
- [ ] `QueueLimit = 0` (reject immediately, don't queue)
- [ ] `TODO: v0.2` comment for Redis migration

## Definition of Done

- [ ] Auth endpoint rate limiting enforced
- [ ] Global rate limiting enforced
