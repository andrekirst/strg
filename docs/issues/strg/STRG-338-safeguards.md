---
id: STRG-338
title: Resource safeguards — pixel cap + timeout + max-source-size
milestone: v0.2
priority: high
status: open
type: feature
labels: [thumbnails, phase-15, safeguards, security]
depends_on: [STRG-334, STRG-336]
blocks: [STRG-343]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-338: Resource safeguards — pixel cap + timeout + max-source-size

## Summary

Three independent pre/in-flight checks that defend the thumbnail subsystem against decompression bombs, runaway generators, and oversized inputs. All three are gated by `ThumbnailOptions` (STRG-334).

## Background / Context

Decision **D14** of issue #52 chose **pixel-area cap + timeout + max-source-size**, in-process for v1, with sandbox process-isolation deferred to STRG-344. The three checks are layered:

1. **Source-size cap** (cheapest) — `FileVersion.Size > MaxSourceSizeBytes`. Rejects before any I/O.
2. **Pixel-area cap** — `MagickImageInfo` header probe. Rejects after a 64 KiB read but before full decode.
3. **Timeout** — `CancellationTokenSource(GenerationTimeoutSeconds)` linked with the consumer's CT. Rejects whatever's still running.

## Technical Specification

### Source-size cap — in `ThumbnailGenerationConsumer` (STRG-331)

```csharp
if (version.Size > options.Value.MaxSourceSizeBytes)
{
    foreach (var variant in options.Value.Variants)
    {
        repo.Add(new ThumbnailEntry {
            FileVersionId = version.Id, Variant = variant, Format = "webp",
            Status = ThumbnailStatus.Unsupported,
            ErrorReason = "too-large",
            GeneratorVersion = ThumbnailGenerator.Version,
        });
    }
    try { await db.SaveChangesAsync(cancellationToken); }
    catch (DbUpdateException ex) when (IsThumbnailUniqueViolation(ex)) { db.ChangeTracker.Clear(); }
    metrics.IncrementThumbnailSkipped("too-large");
    return;
}
```

Rejected BEFORE any blob read. One row per variant (consumer's normal per-variant idempotency loop).

### Pixel-area cap — in `MagickNetImageThumbnailer` (STRG-336)

Already specified in STRG-336 — the `MagickImageInfo` probe runs on the first 64 KiB. On exceeded:

```csharp
return new ThumbnailGenerationOutcome.ResourceLimitExceeded(
    $"pixel-cap ({pixelArea} > {options.Value.MaxPixelArea})");
```

The consumer maps this to `Status = Unsupported, ErrorReason = "pixel-cap"` + `metrics.IncrementThumbnailSkipped("pixel-cap")`.

### Timeout — `CancellationTokenSource` in `MagickNetImageThumbnailer`

```csharp
using var timeoutCts = new CancellationTokenSource(
    TimeSpan.FromSeconds(options.Value.GenerationTimeoutSeconds));
using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

// Pass `linked.Token` to all I/O and to Task.Run(...) wrapping Magick.NET calls.

catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
{
    return new ThumbnailGenerationOutcome.TimedOut(
        TimeSpan.FromSeconds(options.Value.GenerationTimeoutSeconds));
}
```

Distinguishes timeout (`timeoutCts.IsCancellationRequested`) from caller cancellation (`cancellationToken.IsCancellationRequested`) — a caller-cancelled call should NOT be treated as a timeout (the message just goes back to the queue for retry).

The consumer maps `TimedOut` to `Status = Failed, ErrorReason = "timeout"` + `metrics.IncrementThumbnailGenerated(format, variant, "timed-out")`.

### What is NOT in v1

- **In-process memory hard-cap.** Magick.NET does not expose a per-image memory budget API that we trust. The pixel-cap is the proxy. Process-isolation (STRG-344) is the proper fix.
- **Per-tenant rate limiting.** Existing rate limiting (STRG-082) covers HTTP; consumer-side rate limiting is not in scope. If thumbnail generation becomes a noisy-neighbor issue, MassTransit's `concurrentMessageLimit` is the lever.
- **Decode-time pixel cap from the decoder library.** Magick.NET has `ResourceLimits.Memory`, `ResourceLimits.Width`, `ResourceLimits.Height` — these are global (process-wide). Setting them at startup IS reasonable as belt-and-braces; document as a follow-up if not done in this PR.

## Acceptance Criteria

- [ ] Source-size check happens BEFORE any `IStorageProvider.ReadAsync` call for the source.
- [ ] Pixel-cap check uses `MagickImageInfo` header probe, NOT a full decode.
- [ ] Timeout uses `CancellationTokenSource` linked with the consumer CT.
- [ ] Timeout distinguishes from caller cancellation (test coverage).
- [ ] Each safeguard maps to a specific `ThumbnailEntry.Status` + `ErrorReason` + metric reason.
- [ ] Defaults are: `256 MiB` / `100 MP` / `30 s`.

## Test Cases

- **TC-001**: Upload a 300 MiB file with default config → 3 `Unsupported{too-large}` rows; no source read; `strg_thumbnails_skipped_total{reason=too-large}` += 1.
- **TC-002**: Upload a 100 MP PNG bomb → `Unsupported{pixel-cap}` rows; source body NOT decoded (verified by checking `MagickImage.TotalPixels` was never queried — or by timing: the test should complete in milliseconds, not seconds).
- **TC-003**: A test generator that sleeps 60 s with default 30 s timeout → `Failed{timeout}` row; `strg_thumbnails_generated_total{status=timed-out}` += 1.
- **TC-004**: Caller cancels mid-generation (e.g., consumer shutdown) → consumer treats it as a retryable abort, NOT as `Failed{timeout}`. Row stays `Pending` (or absent if no row was inserted yet); message goes back to the queue.
- **TC-005**: `MaxSourceSizeBytes` config override = 1 KiB → tiny image upload still gets `too-large` (validates the gate, not just the default).

## Implementation Tasks

- [ ] Source-size gate in `ThumbnailGenerationConsumer.ProcessAsync`.
- [ ] Pixel-cap probe in `MagickNetImageThumbnailer.GenerateAsync` (already in STRG-336 spec; verify present).
- [ ] Timeout via linked CTS in `MagickNetImageThumbnailer.GenerateAsync` (already in STRG-336 spec; verify present).
- [ ] Tests under `tests/Strg.Integration.Tests/Thumbnails/SafeguardsTests.cs` covering all five test cases.

## Security Review Checklist

- [ ] Pixel-cap is enforced BEFORE full decode — bomb-resistant.
- [ ] Source-size cap is enforced BEFORE any blob I/O — minimizes attack surface.
- [ ] Timeout cannot be set to 0 by config (validator rejects, STRG-334).
- [ ] No way to bypass any of the three caps via a hand-crafted event payload (event has no fields that override config).
- [ ] Caller-cancellation vs timeout is distinguished — a misclassification could cause infinite retries on legitimate cancellations.

## Code Review Checklist

- [ ] `using` for both `CancellationTokenSource` (timeout + linked).
- [ ] Linked CTS uses `CreateLinkedTokenSource(consumerCt, timeoutCt.Token)`.
- [ ] No `Task.Wait`, no `.Result` — all async.
- [ ] Pixel-cap arithmetic uses `long` to avoid `int` overflow on 100 MP+.

## Definition of Done

- [ ] All acceptance criteria green.
- [ ] Tests pass.
- [ ] Manual smoke: configure `Thumbnails:MaxSourceSizeBytes = 1024`, upload a 2 KiB image, observe `Unsupported{too-large}` row.
