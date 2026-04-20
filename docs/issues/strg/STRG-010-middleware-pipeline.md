---
id: STRG-010
title: Configure ASP.NET Core middleware pipeline (HTTPS, CORS, rate limiting, security headers)
milestone: v0.1
priority: critical
status: open
type: infrastructure
labels: [security, api, setup]
depends_on: [STRG-001]
blocks: [STRG-083]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-010: Configure ASP.NET Core middleware pipeline

## Summary

Configure the full ASP.NET Core middleware pipeline in the correct order: HTTPS redirection, security headers, CORS, rate limiting, authentication, authorization. The order is critical for security.

## Technical Specification

### Correct middleware order in `Program.cs`:

```csharp
app.UseHttpsRedirection();
app.UseHsts(); // production only

// Security headers
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    ctx.Response.Headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    await next();
});

app.UseCors("strg-cors");
app.UseSerilogRequestLogging();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGraphQL();
app.MapHealthChecks("/health/ready");
app.MapHealthChecks("/health/live");
app.MapPrometheusScrapingEndpoint("/metrics");
```

### CORS configuration:

```csharp
builder.Services.AddCors(options =>
{
    options.AddPolicy("strg-cors", policy =>
    {
        var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        policy.WithOrigins(origins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});
```

### Rate limiting:

Use ASP.NET Core `RateLimiterMiddleware` with fixed window per endpoint category. Store: in-memory (v0.1), Redis (v0.3).

## Acceptance Criteria

- [ ] HTTP requests redirected to HTTPS
- [ ] HSTS header set in production (`max-age=31536000; includeSubDomains`)
- [ ] `X-Content-Type-Options: nosniff` on all responses
- [ ] `X-Frame-Options: DENY` on all responses
- [ ] CORS allows only configured origins
- [ ] Rate limiter middleware registered before auth middleware
- [ ] Auth middleware registered before authorization middleware
- [ ] Health check endpoints bypass rate limiting
- [ ] `/metrics` endpoint bypass auth and rate limiting
- [ ] No CORS wildcards in production

## Test Cases

- **TC-001**: HTTP request → 301/308 redirect to HTTPS
- **TC-002**: Response → includes `X-Content-Type-Options: nosniff` header
- **TC-003**: CORS preflight from allowed origin → 200 with CORS headers
- **TC-004**: CORS preflight from disallowed origin → 400/403
- **TC-005**: 100 rapid requests to same endpoint → 429 after threshold
- **TC-006**: `/health/ready` → not rate limited
- **TC-007**: Unauthenticated request to protected endpoint → 401

## Implementation Tasks

- [ ] Configure HTTPS redirection and HSTS
- [ ] Add security header middleware
- [ ] Configure CORS with allowed origins from config
- [ ] Configure rate limiter with per-endpoint policies
- [ ] Register middleware in correct order
- [ ] Exempt health check and metrics from rate limiting
- [ ] Write integration tests for middleware behavior

## Security Review Checklist

- [ ] Middleware order is verified: rate limit before auth (prevents auth bypass via rate limit exploit)
- [ ] CORS does not allow `*` origin in any configuration
- [ ] HSTS preload is NOT set (risky for new domains)
- [ ] `X-Powered-By` header is removed
- [ ] `Server` header is removed (Kestrel default)

## Code Review Checklist

- [ ] Middleware order matches documented order in `docs/architecture/01-system-overview.md`
- [ ] Rate limit policies are defined as named constants (not magic numbers inline)
- [ ] CORS origins are configurable, not hardcoded

## Definition of Done

- [ ] All middleware configured and tested
- [ ] Security headers verified with a browser dev tools check
- [ ] Rate limiting verified with a load test tool (hey, wrk, or k6)
