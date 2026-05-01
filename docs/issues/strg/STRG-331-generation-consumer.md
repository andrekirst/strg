---
id: STRG-331
title: ThumbnailGenerationConsumer + encrypted-drive guard + dead-letter observer
milestone: v0.2
priority: high
status: open
type: feature
labels: [thumbnails, phase-15, masstransit, consumer]
depends_on: [STRG-329, STRG-330, STRG-334, STRG-336, STRG-061]
blocks: [STRG-332, STRG-333, STRG-339, STRG-341, STRG-342]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: large
---

# STRG-331: ThumbnailGenerationConsumer + encrypted-drive guard + dead-letter observer

## Summary

The async consumer that turns a `FileUploadedEvent` (or backfill `ThumbnailGenerationRequestedEvent`) into N `ThumbnailEntry` rows + N blobs in storage. Includes the encrypted-drive carve-out (D17), the SQLSTATE-23505 idempotency mirror of `AuditLogConsumer`, and a `IConsumer<Fault<...>>` dead-letter observer.

## Background / Context

Decision **D1** of issue #52 chose **async** generation — upload latency must not be coupled to image processing. The consumer is the single hand-off from the upload flow's outbox event to the per-variant thumbnail rows. It MUST be idempotent under at-least-once redelivery (the existing MassTransit retry/back-off at `src/Strg.Infrastructure/Messaging/MassTransitExtensions.cs:143-148` is 5× exponential 1→30s before dead-letter — inherited automatically).

Decision **D17** is a carve-out: encrypted drives produce a single `Unsupported{encrypted-drive-not-yet-supported}` row and exit. There is no public `IEncryptingFileReader` today — `ChunkedGcmDecryptStream` is `internal sealed` (`src/Strg.Infrastructure/Storage/Encryption/ChunkedGcmDecryptStream.cs:23`) and used only inside `AesGcmFileWriter`'s self-test. STRG-347 will extract a public read-side decryption abstraction for a future Phase 17 thumbnail update.

## Technical Specification

### File — `src/Strg.Infrastructure/Messaging/Consumers/ThumbnailGenerationConsumer.cs`

```csharp
public sealed class ThumbnailGenerationConsumer(
    StrgDbContext db,
    IThumbnailGeneratorRegistry registry,
    IStorageProvider storageProvider,
    IThumbnailRepository repo,
    IPublishEndpoint bus,
    IOptions<ThumbnailOptions> options,
    StrgMetrics metrics,
    ILogger<ThumbnailGenerationConsumer> logger,
    TimeProvider clock)
    : IConsumer<FileUploadedEvent>,
      IConsumer<ThumbnailGenerationRequestedEvent>
{
    public Task Consume(ConsumeContext<FileUploadedEvent> ctx) =>
        ProcessAsync(ctx.Message.TenantId, ctx.Message.FileId, ctx.CancellationToken);

    public Task Consume(ConsumeContext<ThumbnailGenerationRequestedEvent> ctx) =>
        ProcessAsync(ctx.Message.TenantId, ctx.Message.FileId, ctx.CancellationToken);

    private async Task ProcessAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken) { ... }
}
```

The two `IConsumer<T>` implementations share a single private `ProcessAsync` so the `FileUploadedEvent` upload flow and the `ThumbnailGenerationRequestedEvent` backfill flow exercise identical idempotency / metrics / retry behavior.

### Algorithm

1. **Tenant scoping** — set `ITenantContext.TenantId = tenantId` from the event payload (consumer scope has empty ambient context per CLAUDE.md).
2. **Resolve the file's latest `FileVersion`** (`db.FileVersions.AsNoTracking().Where(v => v.FileId == fileId).OrderByDescending(v => v.CreatedAt).FirstOrDefaultAsync`).
3. **Resolve the drive** (no `IgnoreQueryFilters` — global filter is correct here).
4. **Encrypted-drive guard** (D17):
   ```csharp
   if (drive.EncryptionEnabled)
   {
       repo.Add(new ThumbnailEntry {
           FileVersionId = version.Id, Variant = ThumbnailVariants.Thumb, Format = "webp",
           Status = ThumbnailStatus.Unsupported,
           ErrorReason = "encrypted-drive-not-yet-supported",
           GeneratorVersion = ThumbnailGenerator.Version,
       });
       try { await db.SaveChangesAsync(cancellationToken); }
       catch (DbUpdateException ex) when (IsThumbnailUniqueViolation(ex)) { db.ChangeTracker.Clear(); }
       metrics.IncrementThumbnailSkipped("encrypted-drive");
       return;
   }
   ```
   Single row only — variant `thumb`, format `webp` — picked deterministically so re-delivery hits the same idempotency key.
5. **Source-size cap** (per STRG-338) — `if (version.Size > options.Value.MaxSourceSizeBytes) → Unsupported{too-large} + skip metric`.
6. **Read first ~64 bytes** for magic-byte sniffing — `await using var head = await storageProvider.ReadAsync(version.StorageKey, 0, cancellationToken)` then `var headBytes = new byte[64]; await head.ReadExactlyAsync(headBytes, ...)` (or however many bytes the file has).
7. **Sniff** — `MimeSniffer.Detect(headBytes)`. If `null` → `Unsupported{unknown-mime}` + skip metric, return.
8. **Resolve generator** — `registry.Resolve(sniffedMime, headBytes)`. If `null` → `Unsupported{no-generator}` + skip metric.
9. **For each variant in `options.Value.Variants`** (independent transaction per variant — failure on variant 2 doesn't roll back variant 1):
   - Insert `ThumbnailEntry { Status = Pending, ... }` and `SaveChangesAsync`. On `IsThumbnailUniqueViolation` → row already exists from a prior delivery; load the existing row. If `Status == Ready` → return early (no-op). If `Status == Pending` from a stale prior attempt → load and continue.
   - Open a fresh source stream (`storageProvider.ReadAsync(version.StorageKey, 0, ct)` — generator MAY consume the head bytes from this fresh stream, not the sniffer's).
   - Call `generator.GenerateAsync(source, request, linkedCt)` where `linkedCt` is `cancellationToken` linked with a `CancellationTokenSource(options.Value.GenerationTimeoutSeconds)`.
   - On `Success`: `await using var output = result.Output;` → `var key = ThumbnailStorageKeyBuilder.Build(...); var path = StoragePath.Parse(key); await storageProvider.WriteAsync(path.Value, output, ct);` → update entry `Status = Ready, StorageKey = path.Value, Width, Height, SizeBytes, GeneratedAt = clock.GetUtcNow()`. Publish `ThumbnailReadyEvent` to outbox BEFORE `SaveChangesAsync`. `metrics.IncrementThumbnailGenerated(format, variant, "ready")` + `metrics.RecordThumbnailDuration`.
   - On `Unsupported / SourceCorrupt`: update entry `Status = Unsupported, ErrorReason = reason`. `metrics.IncrementThumbnailSkipped(reason)`.
   - On `ResourceLimitExceeded`: `Status = Unsupported, ErrorReason = "pixel-cap"`. `metrics.IncrementThumbnailSkipped("pixel-cap")`.
   - On `TimedOut`: `Status = Failed, ErrorReason = "timeout"`. `metrics.IncrementThumbnailGenerated(format, variant, "timed-out")`.

### Idempotency — `IsThumbnailUniqueViolation`

```csharp
internal static bool IsThumbnailUniqueViolation(DbUpdateException ex) =>
    ex.InnerException is PostgresException pg
    && pg.SqlState == "23505"
    && pg.ConstraintName == ThumbnailConstraintNames.UniqueIndex;
```

Equality match (NOT substring) — same triangulation as `AuditLogConsumer.IsEventIdUniqueViolation` at `src/Strg.Infrastructure/Messaging/Consumers/AuditLogConsumer.cs:220-233`. On hit: `db.ChangeTracker.Clear()`, log at debug ("re-delivery, row already present"), return.

### Dead-letter observer

```csharp
public sealed class ThumbnailGenerationFaultObserver(ILogger<ThumbnailGenerationFaultObserver> logger)
    : IConsumer<Fault<FileUploadedEvent>>,
      IConsumer<Fault<ThumbnailGenerationRequestedEvent>>
{
    // logs { TenantId, FileId, Exceptions } — NO PII, no MIME, no path
}
```

Logged at warn. The fault arrives after the 5× retry exhausts.

### DI registration — `src/Strg.Api/Program.cs`

```csharp
busCfg.AddConsumer<ThumbnailGenerationConsumer>();
busCfg.AddConsumer<ThumbnailGenerationFaultObserver>();
```

Retry/back-off inherited automatically from `MassTransitExtensions.UseStrgConsumerDefaults`.

## Acceptance Criteria

- [ ] Consumer implements both `IConsumer<FileUploadedEvent>` and `IConsumer<ThumbnailGenerationRequestedEvent>` and shares a single private orchestration method.
- [ ] Encrypted-drive (`drive.EncryptionEnabled == true`) produces exactly one `ThumbnailEntry{Status=Unsupported, ErrorReason="encrypted-drive-not-yet-supported"}` and a `strg_thumbnails_skipped_total{reason=encrypted-drive}` increment.
- [ ] Re-delivery of the same event produces no additional rows and no additional generation work — idempotency via `IsThumbnailUniqueViolation` (SQLSTATE 23505 + exact `ConstraintName` equality).
- [ ] Per-variant transaction scope: failure on variant N does not roll back variants 1…N-1.
- [ ] Generation timeout writes `Status=Failed, ErrorReason="timeout"` and increments `strg_thumbnails_generated_total{status=timed-out}`.
- [ ] `ThumbnailReadyEvent` is published to the outbox (`IPublishEndpoint`) BEFORE `SaveChangesAsync` so the publish + state change are atomic.
- [ ] Tenant is read from the event payload, not ambient context.
- [ ] Source stream and output stream are streamed end-to-end — no `byte[]` buffering.
- [ ] Dead-letter observer logs `{TenantId, FileId, Exceptions}` only — no MIME, no path, no PII.
- [ ] All thumbnail keys flow through `StoragePath.Parse()` before reaching `IStorageProvider`.

## Test Cases

- **TC-001**: Upload an encrypted-drive file → publish `FileUploadedEvent` → exactly one `Unsupported{encrypted-drive-not-yet-supported}` row, no blobs, `strg_thumbnails_skipped_total{reason=encrypted-drive}` += 1.
- **TC-002**: Same event published twice → 1 set of N rows (not 2N); `strg_thumbnails_generated_total` increments N (not 2N).
- **TC-003**: Generator throws `OperationCanceledException` after timeout → row at `Failed{timeout}`, metric `strg_thumbnails_generated_total{status=timed-out}` += 1, no blob written.
- **TC-004**: `MimeSniffer.Detect` returns `null` (e.g., `.txt` upload) → `Unsupported{unknown-mime}` rows for every variant.
- **TC-005**: Variant 2 generator throws unexpected exception → variant 1's `Ready` row is committed; consumer message goes to retry/dead-letter for variant 2.
- **TC-006**: Backfill `ThumbnailGenerationRequestedEvent` for a file that already has `Ready` rows → no-op (idempotency); no extra audit entry from `AuditLogConsumer` (which only handles `FileUploadedEvent` not the backfill event).

## Implementation Tasks

- [ ] Add `ThumbnailGenerationConsumer` with both `IConsumer` interfaces and the shared private `ProcessAsync`.
- [ ] Add `IsThumbnailUniqueViolation` mirroring `AuditLogConsumer.IsEventIdUniqueViolation`.
- [ ] Add `ThumbnailGenerationFaultObserver`.
- [ ] Register both in `Program.cs`.
- [ ] Wire `IThumbnailService` (Infrastructure impl) to call into the consumer's logic OR have the consumer call into `IThumbnailService` — choose one and document.
- [ ] Integration tests under `tests/Strg.Integration.Tests/Thumbnails/ThumbnailGenerationConsumerTests.cs` covering all six test cases.

## Security Review Checklist

- [ ] No `IgnoreQueryFilters()` anywhere in the consumer — tenant filter is in force.
- [ ] Tenant set from event payload, not ambient context.
- [ ] Encrypted-drive carve-out cannot be bypassed by a malformed event (tested in TC-001).
- [ ] User-controlled bytes (sniffed MIME, image content) never flow into log messages or exception text.
- [ ] Dead-letter log fields are bounded — no `Exception.ToString()` directly when it might contain stack frames with user paths (use `ex.GetType().FullName` + `ex.Message` only).
- [ ] All paths go through `StoragePath.Parse()` before `IStorageProvider`.

## Code Review Checklist

- [ ] `IsThumbnailUniqueViolation` uses **equality** not `Contains` for `ConstraintName`.
- [ ] Per-variant transaction scope — explicit `BeginTransactionAsync` / `CommitAsync` if needed to ensure isolation.
- [ ] `IDomainEvent` published BEFORE `SaveChangesAsync` (outbox semantics).
- [ ] No `Thread.Sleep`; cancellation linked via `CancellationTokenSource.CreateLinkedTokenSource`.
- [ ] Streams are `await using`-ed.
- [ ] No `new HttpClient()` (irrelevant here, but checked).

## Definition of Done

- [ ] All acceptance criteria green.
- [ ] Integration tests pass.
- [ ] `MassTransitExtensions` retry config covers the new consumer (5× exponential 1→30s before dead-letter).
- [ ] Dead-letter fault observer is registered and tested.
