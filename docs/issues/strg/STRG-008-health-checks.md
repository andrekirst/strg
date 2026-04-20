---
id: STRG-008
title: Configure ASP.NET Core health checks
milestone: v0.1
priority: medium
status: open
type: infrastructure
labels: [observability, kubernetes]
depends_on: [STRG-004]
blocks: []
assigned_agent_type: general-purpose
estimated_complexity: small
---

# STRG-008: Configure ASP.NET Core health checks

## Summary

Configure `/health/ready` (readiness: database reachable, storage accessible) and `/health/live` (liveness: process alive) endpoints for Kubernetes probe compatibility.

## Technical Specification

```csharp
builder.Services.AddHealthChecks()
    .AddDbContextCheck<StrgDbContext>("database")
    .AddCheck<StorageHealthCheck>("storage");

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false  // liveness = process alive, no checks
});
```

### `StorageHealthCheck`: verifies that the default local FS drive root is writable.

## Acceptance Criteria

- [ ] `/health/live` returns 200 always (process alive)
- [ ] `/health/ready` returns 200 when database is reachable
- [ ] `/health/ready` returns 503 when database is unreachable
- [ ] `/health/ready` returns JSON response with component details
- [ ] Health endpoints do not require authentication
- [ ] Health endpoints are excluded from rate limiting

## Test Cases

- **TC-001**: Database connected → `/health/ready` returns 200
- **TC-002**: Database connection string invalid → `/health/ready` returns 503
- **TC-003**: `/health/live` always returns 200 regardless of DB state

## Implementation Tasks

- [ ] Install `AspNetCore.HealthChecks.UI.Client`
- [ ] Register health checks in `Program.cs`
- [ ] Implement `StorageHealthCheck`
- [ ] Map endpoints with appropriate filters
- [ ] Exclude health endpoints from rate limiting middleware

## Security Review Checklist

- [ ] Health check responses do not expose stack traces or internal paths
- [ ] Health endpoints do not require auth (Kubernetes probes cannot authenticate)
- [ ] Response does not reveal database connection string details

## Definition of Done

- [ ] Both endpoints return correct status codes
- [ ] Manual Kubernetes probe simulation succeeds
