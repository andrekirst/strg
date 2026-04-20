---
id: STRG-084
title: Configure HTTP security headers and HSTS
milestone: v0.1
priority: high
status: open
type: implementation
labels: [security, api, infrastructure]
depends_on: [STRG-010]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-084: Configure HTTP security headers and HSTS

## Summary

Configure HTTP response security headers and HSTS for the strg API. Since strg is an API (no UI in v0.1), headers like `Content-Security-Policy` and `X-Frame-Options` are still important for defense-in-depth in case a browser hits the API or documentation endpoint.

## Technical Specification

### Headers middleware (in `Program.cs`, after HTTPS redirection):

```csharp
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    ctx.Response.Headers["X-Permitted-Cross-Domain-Policies"] = "none";
    ctx.Response.Headers["Permissions-Policy"] =
        "camera=(), microphone=(), geolocation=(), interest-cohort=()";

    // API-specific CSP: no scripts, no inline, no iframes
    ctx.Response.Headers["Content-Security-Policy"] =
        "default-src 'none'; frame-ancestors 'none';";

    await next(ctx);
});
```

### HSTS (via `UseHsts()` in `Program.cs`):

```csharp
if (!app.Environment.IsDevelopment())
{
    app.UseHsts(); // Adds Strict-Transport-Security header (max-age=31536000)
}
app.UseHttpsRedirection();
```

### HSTS configuration in `appsettings.json`:

```json
{
  "Hsts": {
    "MaxAge": 31536000,
    "IncludeSubDomains": true,
    "Preload": false
  }
}
```

### CORS configuration:

```csharp
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowedOrigins", policy =>
    {
        policy
            .WithOrigins(builder.Configuration
                .GetSection("Cors:AllowedOrigins")
                .Get<string[]>() ?? [])
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});
```

## Headers to configure:

| Header | Value | Purpose |
|---|---|---|
| `X-Content-Type-Options` | `nosniff` | Prevent MIME sniffing |
| `X-Frame-Options` | `DENY` | Prevent clickjacking |
| `Referrer-Policy` | `strict-origin-when-cross-origin` | Limit referrer info |
| `Content-Security-Policy` | `default-src 'none'` | API: no scripts needed |
| `Strict-Transport-Security` | `max-age=31536000; includeSubDomains` | HTTPS enforcement |
| `Permissions-Policy` | No camera/mic/geo | Deny browser features |

## Acceptance Criteria

- [ ] `GET /health/live` response includes `X-Content-Type-Options: nosniff`
- [ ] `GET /health/live` response includes `X-Frame-Options: DENY`
- [ ] HTTPS response includes `Strict-Transport-Security` header
- [ ] CORS only allows origins from `appsettings.json`
- [ ] No `Server` header in responses (information leakage)
- [ ] No `X-Powered-By` header

## Test Cases

- **TC-001**: Any API response → `X-Content-Type-Options: nosniff` present
- **TC-002**: HTTPS response → `Strict-Transport-Security` present in non-development
- **TC-003**: CORS preflight from allowed origin → `200`
- **TC-004**: CORS from unlisted origin → no `Access-Control-Allow-Origin` header

## Implementation Tasks

- [ ] Add security headers middleware in `Program.cs`
- [ ] Configure `UseHsts()` and `UseHttpsRedirection()`
- [ ] Configure CORS with allowed origins from config
- [ ] Remove `Server` header via Kestrel options

## Testing Tasks

- [ ] Integration test: verify headers present on API response
- [ ] Integration test: CORS from invalid origin returns no ACAO header

## Security Review Checklist

- [ ] `Preload: false` for HSTS in v0.1 (domain not yet submitted to preload list)
- [ ] CSP `default-src 'none'` appropriate for pure API
- [ ] `Server` header removed (Kestrel: `AddServerHeader = false`)

## Code Review Checklist

- [ ] Headers set for all responses (middleware before routes)
- [ ] `UseHsts()` not called in development (breaks localhost dev)
- [ ] CORS origins configurable from `appsettings.json`

## Definition of Done

- [ ] All security headers present in API responses
- [ ] HSTS enabled in non-development environments
