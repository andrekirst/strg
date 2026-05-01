---
id: STRG-333
title: strg_thumbnails_* metrics extension to StrgMetrics
milestone: v0.2
priority: medium
status: open
type: feature
labels: [thumbnails, phase-15, observability, metrics]
depends_on: [STRG-331]
blocks: [STRG-343]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-333: strg_thumbnails_* metrics extension to StrgMetrics

## Summary

Extend the existing `StrgMetrics` helper with thumbnail-specific counters, histogram, and an UpDownCounter. No new `Meter` instance — reuses `StrgMetrics.MeterName = "Strg"`.

## Background / Context

Per CLAUDE.md and the STRG-007 precedent, all observability flows through the singleton `StrgMetrics` helper at `src/Strg.Infrastructure/Observability/StrgMetrics.cs:20-47`. `/metrics` is anonymous — no PII, no high-cardinality labels (no `tenantId`, no `userId`, no MIME-from-content).

## Technical Specification

### Additions to `src/Strg.Infrastructure/Observability/StrgMetrics.cs`

```csharp
// Counters
public Counter<long> ThumbnailsGeneratedTotal { get; }   // labels: format, variant, status
public Counter<long> ThumbnailsSkippedTotal { get; }     // labels: reason

// Histogram
public Histogram<double> ThumbnailsGenerationDurationSeconds { get; }  // labels: format, unit "s"

// UpDownCounter
public UpDownCounter<long> ThumbnailsInflight { get; }   // no labels
```

Constructor (additions inside the existing constructor — no new `Meter`):

```csharp
ThumbnailsGeneratedTotal = _meter.CreateCounter<long>(
    "strg_thumbnails_generated_total",
    unit: null,
    description: "Thumbnail generation outcomes (per variant, per format, per status).");

ThumbnailsSkippedTotal = _meter.CreateCounter<long>(
    "strg_thumbnails_skipped_total",
    unit: null,
    description: "Thumbnail generation skipped (reason: encrypted-drive, too-large, pixel-cap, unknown-mime, no-generator).");

ThumbnailsGenerationDurationSeconds = _meter.CreateHistogram<double>(
    "strg_thumbnails_generation_duration_seconds",
    unit: "s",
    description: "Thumbnail generation wall time, per format.");

ThumbnailsInflight = _meter.CreateUpDownCounter<long>(
    "strg_thumbnails_inflight",
    unit: null,
    description: "Concurrent thumbnail generations in progress.");
```

### Helper methods

```csharp
public void IncrementThumbnailGenerated(string format, string variant, string status) =>
    ThumbnailsGeneratedTotal.Add(1,
        new KeyValuePair<string, object?>("format", format),
        new KeyValuePair<string, object?>("variant", variant),
        new KeyValuePair<string, object?>("status", status));   // "ready" | "timed-out"

public void IncrementThumbnailSkipped(string reason) =>
    ThumbnailsSkippedTotal.Add(1, new KeyValuePair<string, object?>("reason", reason));

public void RecordThumbnailDuration(string format, double seconds) =>
    ThumbnailsGenerationDurationSeconds.Record(seconds,
        new KeyValuePair<string, object?>("format", format));
```

### Label cardinality budget

| Label | Cardinality | Source |
|---|---|---|
| `format` | 2 (`webp`, `jpeg` in v1) — bounded | enum-like whitelist |
| `variant` | 3 (`thumb`, `small`, `medium`) — bounded | `ThumbnailVariants.All` |
| `status` | 2 (`ready`, `timed-out`) | hard-coded |
| `reason` | 5 (`encrypted-drive`, `too-large`, `pixel-cap`, `unknown-mime`, `no-generator`) | hard-coded |

Total cross-product is bounded — no PII, no user-derived strings.

## Acceptance Criteria

- [ ] All four instruments are added to `StrgMetrics` using the existing `_meter` field.
- [ ] No new `Meter` instance — `MeterName = "Strg"` is reused.
- [ ] Helper methods enforce the label whitelist via parameter types or runtime asserts (the consumer at STRG-331 only passes from a hard-coded set).
- [ ] Histogram unit is `"s"` (seconds), per OTel semantic conventions.
- [ ] No tenant/user/file/path labels anywhere.

## Test Cases

- **TC-001**: After a successful generation, `strg_thumbnails_generated_total{format=webp,variant=small,status=ready}` is `1`.
- **TC-002**: After a 100 MP bomb, `strg_thumbnails_skipped_total{reason=pixel-cap}` is `1`.
- **TC-003**: Histogram `strg_thumbnails_generation_duration_seconds{format=webp}` records a value in the expected range (>0, <30) for a real test image.
- **TC-004**: `strg_thumbnails_inflight` increments before generation starts and decrements after (verified via concurrent test).
- **TC-005**: `/metrics` (anonymous) does NOT emit any tenant/user/file labels for the thumbnail counters.

## Implementation Tasks

- [ ] Extend `StrgMetrics` with the four instruments + helper methods.
- [ ] Inject `StrgMetrics` into `ThumbnailGenerationConsumer` (already done in STRG-331).
- [ ] Test under `tests/Strg.Integration.Tests/Thumbnails/ThumbnailMetricsTests.cs` (or fold into the consumer integration tests).

## Security Review Checklist

- [ ] No PII labels. Verified by reading the helper method implementations.
- [ ] No user-controlled strings as label values — only enum-like whitelisted constants.
- [ ] `/metrics` remains anonymous post-PR (no auth requirement added accidentally).

## Code Review Checklist

- [ ] Metric names follow `strg_*` convention.
- [ ] Unit on histogram is `"s"`.
- [ ] Counter unit is `null` (not `"By"` — these are events, not bytes).
- [ ] Helper methods are `public` so test code can assert directly.

## Definition of Done

- [ ] All acceptance criteria green.
- [ ] `dotnet test` passes.
- [ ] Manual `/metrics` smoke shows the new instruments after a test upload.
