---
id: STRG-008
title: Configure ASP.NET Core health checks
milestone: v0.1
priority: medium
status: done
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

- [x] `/health/live` returns 200 always (process alive) — `Program.cs:266-269` (`Predicate = _ => false`)
- [x] `/health/ready` returns 200 when database is reachable — pinned by `HealthCheckEndpointTests.Get_health_ready_without_auth_returns_200_when_db_reachable`
- [x] `/health/ready` returns 503 when database is unreachable — pinned by `UnreachableDbHealthCheckTests.Get_health_ready_returns_503_when_db_unreachable`
- [x] `/health/ready` returns JSON response with component details — `SafeHealthCheckResponseWriter.WriteAsync` (custom writer, drops Exception/Data)
- [x] Health endpoints do not require authentication — `.AllowAnonymous()` on both `MapHealthChecks` calls (`Program.cs:269,275`)
- [x] Health endpoints are excluded from rate limiting — vacuously true (no rate limiter registered today); `Program.cs:262-265` documents the future obligation

## Test Cases

- [x] **TC-001**: Database connected → `/health/ready` returns 200 — `HealthCheckEndpointTests.Get_health_ready_without_auth_returns_200_when_db_reachable`
- [x] **TC-002**: Database connection string invalid → `/health/ready` returns 503 — `UnreachableDbHealthCheckTests.Get_health_ready_returns_503_when_db_unreachable` (RFC 5737 `192.0.2.1` host with `Timeout=2`)
- [x] **TC-003**: `/health/live` always returns 200 regardless of DB state — `HealthCheckEndpointTests.Get_health_live_without_auth_returns_200` + `UnreachableDbHealthCheckTests.Get_health_live_returns_200_when_db_unreachable`

## Implementation Tasks

- [x] ~~Install `AspNetCore.HealthChecks.UI.Client`~~ — DROPPED. Custom `SafeHealthCheckResponseWriter` replaces it: the Xabaril writer serializes `Exception.Message`, which embeds Npgsql `Host=`/`Username=` substrings — would violate the security checklist on a 503 with an unreachable DB. Only `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` 10.0.6 added (Microsoft package, version-aligned with EF Core).
- [x] Register health checks in `Program.cs` — lines 50-63 (tagged `"strg-ready"`, NOT the more common `"ready"`, to exclude MassTransit's auto-registered bus check from the readiness gate; rationale documented inline)
- [x] Implement `StorageHealthCheck` — `src/Strg.Infrastructure/HealthChecks/StorageHealthCheck.cs`. Looks up the default local Drive (carve-out per CLAUDE.md §Security #1), write+delete sentinel via `IStorageProvider`. DB-down → Healthy (avoids double-counting); no drive provisioned → Degraded; write failure → Unhealthy (no `exception:` → no leak via writer).
- [x] Map endpoints with appropriate filters — `Program.cs:266-275` (Predicate, ResponseWriter, AllowAnonymous)
- [x] Exclude health endpoints from rate limiting middleware — vacuously satisfied; comment block at `Program.cs:262-265` reserves `.DisableRateLimiting()` for the future PR that adds `AspNetCore.RateLimiting`

## Security Review Checklist

- [x] Health check responses do not expose stack traces or internal paths — `SafeHealthCheckResponseWriter` omits `Exception` and `Data`; suppresses `Description` when `entry.Exception != null` (defense-in-depth against framework's `description = ex.Message` default for throwing checks). Pinned by `SafeHealthCheckResponseWriterTests.Throwing_check_exception_message_must_not_reach_response_body`.
- [x] Health endpoints do not require auth (Kubernetes probes cannot authenticate) — `.AllowAnonymous()` on both endpoints, pinned by tests asserting `client.DefaultRequestHeaders.Authorization.Should().BeNull()` + 200 response.
- [x] Response does not reveal database connection string details — `UnreachableDbHealthCheckTests` asserts response body excludes `192.0.2.1`, `strg_test`, `Host=`, `Username=`, `Password`, `Npgsql`, `NpgsqlException`, `at ` (stack frame), `--->` (inner exception). Independently re-verified by the throwing-check regression pin.

## Definition of Done

- [x] Both endpoints return correct status codes — verified by integration tests (DB reachable → 200, DB unreachable → 503, /health/live always 200).
- [x] Manual Kubernetes probe simulation succeeds — `WebApplicationFactory.CreateClient()` + `UseTestServer` reproduce the probe shape (anonymous GET, no special headers); the `HealthCheckEndpointTests` and `UnreachableDbHealthCheckTests` are the automated equivalents of the manual K8s probe simulation.
