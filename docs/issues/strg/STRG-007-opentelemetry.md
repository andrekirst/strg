---
id: STRG-007
title: Configure OpenTelemetry metrics and tracing
milestone: v0.1
priority: medium
status: open
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

- [ ] `/metrics` endpoint returns Prometheus-format metrics
- [ ] Every HTTP request produces a trace span with status code
- [ ] EF Core queries produce child spans under HTTP spans
- [ ] OTLP exporter sends traces when `Observability:OtlpEndpoint` is configured
- [ ] Custom upload/download counters increment correctly
- [ ] `/metrics` is NOT protected by auth (scraped by Prometheus)
- [ ] Health check endpoints are excluded from traces (noisy)

## Test Cases

- **TC-001**: Make an HTTP request → trace appears in OTLP endpoint
- **TC-002**: GET `/metrics` → returns content-type `text/plain; version=0.0.4`
- **TC-003**: Upload a file → `strg_uploads_total` increments by 1
- **TC-004**: `/metrics` endpoint returns 200 without Authorization header

## Implementation Tasks

- [ ] Install OpenTelemetry packages
- [ ] Configure tracing in `Program.cs`
- [ ] Configure metrics in `Program.cs`
- [ ] Add custom `Meter` for strg-specific metrics
- [ ] Exclude health check paths from traces
- [ ] Document OTLP endpoint configuration in `appsettings.json`

## Security Review Checklist

- [ ] `/metrics` must not expose sensitive data (user IDs, file names in metric labels)
- [ ] Trace spans must not include request body content
- [ ] OTLP endpoint configuration accepts environment variable override

## Definition of Done

- [ ] `/metrics` returns metrics
- [ ] Traces visible in a local Jaeger instance when tested
