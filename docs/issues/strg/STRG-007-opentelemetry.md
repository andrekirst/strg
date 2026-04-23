---
id: STRG-007
title: Configure OpenTelemetry metrics and tracing
milestone: v0.1
priority: medium
status: done
type: infrastructure
labels: [observability, telemetry]
depends_on: [STRG-001]
blocks: []
assigned_agent_type: general-purpose
estimated_complexity: small
---

# STRG-007: Configure OpenTelemetry metrics and tracing

## Summary

Configure OpenTelemetry with Prometheus metrics export and OTLP trace export. All HTTP requests, database queries, and file operations should produce spans.

## Technical Specification

### Packages: `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.EntityFrameworkCore`, `OpenTelemetry.Exporter.Prometheus.AspNetCore`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`

### Registration in `Program.cs`:

```csharp
// OTLP endpoint resolution: env var takes precedence over appsettings
var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
    ?? config["OpenTelemetry:OtlpEndpoint"]
    ?? "http://localhost:4317";

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddSource("Strg.*")
        .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddPrometheusExporter());

app.MapPrometheusScrapingEndpoint("/metrics");
```

### Custom metrics to add:

- `strg_uploads_total` (counter) — successful uploads
- `strg_upload_bytes_total` (counter) — total bytes uploaded
- `strg_downloads_total` (counter) — successful downloads
- `strg_active_connections` (gauge) — active WebDAV/WebSocket connections

## Acceptance Criteria

- [x] `/metrics` endpoint returns Prometheus-format metrics
- [x] Every HTTP request produces a trace span with status code
- [x] EF Core queries produce child spans under HTTP spans
- [x] OTLP exporter sends traces when `Observability:OtlpEndpoint` is configured
- [x] Custom upload/download counters increment correctly
- [x] `/metrics` is NOT protected by auth (scraped by Prometheus)
- [x] Health check endpoints are excluded from traces (noisy)

## Test Cases

- [x] **TC-001**: Make an HTTP request → trace appears via in-memory exporter (see `tests/Strg.Integration.Tests/Observability/TracingTests.cs`)
- [x] **TC-001b**: EF Core query produces a span (AC-3 coverage)
- [x] **TC-002**: GET `/metrics` → returns content-type `text/plain; version=0.0.4` (see `tests/Strg.Integration.Tests/Observability/PrometheusMetricsTests.cs`)
- [x] **TC-003**: `StrgMetrics.IncrementUploads/Downloads/AddConnection/RemoveConnection` produces expected MeterListener measurements (see `tests/Strg.Api.Tests/Observability/StrgMetricsTests.cs`)
- [x] **TC-004**: `/metrics` returns 200 without Authorization header (assertion pinned in `PrometheusMetricsTests`)

## Implementation Tasks

- [x] Install OpenTelemetry packages (`Directory.Packages.props` + `Strg.Infrastructure.csproj`)
- [x] Configure tracing via `AddStrgObservability` extension (AspNet + EF Core + OTLP)
- [x] Configure metrics via `AddStrgObservability` (AspNet + Runtime + Strg meter + Prometheus)
- [x] Add `StrgMetrics` meter with `strg_uploads_total`, `strg_upload_bytes_total`, `strg_downloads_total`, `strg_active_connections`
- [x] Exclude `/health`, `/healthz`, `/metrics` from tracing via `IsNoiseEndpoint` filter
- [x] Document OTLP endpoint in `appsettings.json` under `Observability:OtlpEndpoint`

## Security Review Checklist

- [x] `/metrics` exposes no sensitive data — instruments carry no user/tenant/filename tags; AspNet default uses route templates (low cardinality); SECURITY remarks on `StrgMetrics` forbid PII tags for future call sites
- [x] Trace spans do not include request body content — only `Filter` option is configured; no `EnrichWithHttpRequest`; EF Core default `SetDbStatementForText = false`
- [x] OTLP endpoint accepts env-var override — `OTEL_EXPORTER_OTLP_ENDPOINT` > `Observability:OtlpEndpoint` > `http://localhost:4317`

## Definition of Done

- [x] `/metrics` returns metrics — TC-002 pins the contract against the running host
- [x] Traces verified via OTel in-memory exporter (TC-001 HTTP span, TC-001b EF Core span); Jaeger visualization deferred to deployment smoke runbook
