---
id: STRG-332
title: ThumbnailCleanupConsumer + extend FileVersionStore.PruneVersionsAsync
milestone: v0.2
priority: high
status: open
type: feature
labels: [thumbnails, phase-15, masstransit, cleanup, prune]
depends_on: [STRG-329, STRG-331]
blocks: [STRG-343]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-332: ThumbnailCleanupConsumer + extend FileVersionStore.PruneVersionsAsync

## Summary

Two cleanup paths for thumbnail rows + blobs: (a) `FileDeletedEvent` consumer that soft-deletes thumbnail rows for all versions of the deleted file and best-effort-deletes the blobs; (b) extension of the existing per-version prune loop in `FileVersionStore.PruneVersionsAsync` so that pruning a `FileVersion` also deletes its thumbnail blobs before the row-removal transaction.

## Background / Context

Issue #52's STRG-332 specifies BOTH cleanup paths in one issue. They share a discipline:

- `IStorageProvider.DeleteAsync` is idempotent by contract (`src/Strg.Plugin.Abstractions/Storage/IStorageProvider.cs:66`) — best-effort deletion never throws on missing keys.
- `ThumbnailEntry` rows have `OnDelete(Cascade)` from `FileVersion` (STRG-329), so DB-level removal happens automatically when a `FileVersion` row is deleted. The cleanup consumer is responsible for **soft-delete** propagation (when a file is soft-deleted but versions remain) and for **blob cleanup**, which the cascade does NOT do.
- The prune loop at `src/Strg.Infrastructure/Versioning/FileVersionStore.cs:160-169` is the existing per-version atomic scope. We **extend** it — we do not replace it.

## Technical Specification

### File-delete consumer — `src/Strg.Infrastructure/Messaging/Consumers/ThumbnailCleanupConsumer.cs`

```csharp
public sealed class ThumbnailCleanupConsumer(
    StrgDbContext db,
    IStorageProvider storageProvider,
    IThumbnailRepository repo,
    ILogger<ThumbnailCleanupConsumer> logger)
    : IConsumer<FileDeletedEvent>
{
    public async Task Consume(ConsumeContext<FileDeletedEvent> ctx)
    {
        var thumbnails = await repo.GetByFileAsync(ctx.Message.FileId, ctx.CancellationToken);
        if (thumbnails.Count == 0) { return; }

        // Best-effort blob delete first — DeleteAsync is idempotent (load-bearing).
        // Failure here MUST NOT block the soft-delete; we'd rather have an orphan blob
        // than a stuck cleanup. Operator-visible via metric.
        foreach (var t in thumbnails.Where(t => t.Status == ThumbnailStatus.Ready))
        {
            try
            {
                var path = StoragePath.Parse(t.StorageKey);
                await storageProvider.DeleteAsync(path.Value, ctx.CancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Best-effort thumbnail blob delete failed for {Key}", t.StorageKey);
            }
        }

        repo.SoftDeleteRange(thumbnails);
        await db.SaveChangesAsync(ctx.CancellationToken);
    }
}
```

Idempotent: re-delivery finds the rows already soft-deleted (hidden by global filter), returns no-op.

### Extension to `FileVersionStore.PruneVersionsAsync`

The existing loop at `src/Strg.Infrastructure/Versioning/FileVersionStore.cs:160-169`:

```csharp
foreach (var version in toPrune)
{
    await provider.DeleteAsync(version.StorageKey, cancellationToken).ConfigureAwait(false);

    await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    db.FileVersions.Remove(version);
    await quotaService.ReleaseAsync(file.CreatedBy, version.Size, cancellationToken).ConfigureAwait(false);
    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
}
```

becomes:

```csharp
foreach (var version in toPrune)
{
    await provider.DeleteAsync(version.StorageKey, cancellationToken).ConfigureAwait(false);

    // STRG-332: enumerate thumbnail blobs BEFORE the per-version transaction so a
    // crash mid-loop leaves the blobs idempotently re-deletable on retry. The
    // ThumbnailEntry rows themselves cascade away when the FileVersion row is
    // deleted inside the transaction (STRG-329's OnDelete(Cascade)).
    var thumbnailKeys = await db.ThumbnailEntries
        .Where(t => t.FileVersionId == version.Id && t.Status == ThumbnailStatus.Ready)
        .Select(t => t.StorageKey)
        .ToListAsync(cancellationToken)
        .ConfigureAwait(false);

    foreach (var key in thumbnailKeys)
    {
        await provider.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
    }

    await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    db.FileVersions.Remove(version);
    await quotaService.ReleaseAsync(file.CreatedBy, version.Size, cancellationToken).ConfigureAwait(false);
    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
}
```

The blob delete stays OUTSIDE the per-version transaction (matches the existing pattern for `version.StorageKey`). The `ThumbnailEntry` rows cascade away when the `FileVersion` row is removed inside the transaction.

### Race condition — prune races with generation

If `PruneVersionsAsync` runs while a `ThumbnailGenerationConsumer` is mid-write:

1. Prune deletes the source `FileVersion` blob.
2. Prune deletes the `FileVersion` row → cascade deletes `ThumbnailEntry` rows.
3. Generation consumer tries to insert a new `ThumbnailEntry` → FK violation → exception → MassTransit retries.
4. On retry, generation consumer reads the file (`db.FileVersions.FirstOrDefaultAsync(...)`) and finds nothing (soft-deleted, hidden by filter) → no-op return.

The race is benign — generation either races and lands a row that immediately cascades away, or skips entirely. No orphan blobs (the cascade fires; the new generation's blob is the only at-risk asset, and the per-variant write is the last step before the row update).

## Acceptance Criteria

- [ ] `ThumbnailCleanupConsumer` consumes `FileDeletedEvent`, soft-deletes thumbnail rows, best-effort-deletes blobs, idempotent on re-delivery.
- [ ] `FileVersionStore.PruneVersionsAsync` enumerates and deletes thumbnail blobs for each pruned version BEFORE the per-version transaction.
- [ ] Cascade on `FileVersion → ThumbnailEntry` (from STRG-329) handles row removal inside the transaction.
- [ ] Best-effort blob deletes log warnings on failure but do NOT block the soft-delete or prune.
- [ ] All thumbnail keys flow through `StoragePath.Parse()` before `IStorageProvider.DeleteAsync`.
- [ ] No new `IgnoreQueryFilters` calls anywhere in this PR.

## Test Cases

- **TC-001**: Soft-delete a file with 3 thumbnail rows → all 3 rows soft-deleted, 3 blob delete calls fired, consumer succeeds.
- **TC-002**: Re-deliver `FileDeletedEvent` → consumer finds 0 thumbnails (already soft-deleted) → no-op, no error.
- **TC-003**: Best-effort blob delete throws (provider error) → consumer logs warning, soft-deletes rows anyway, succeeds.
- **TC-004**: Upload 4 versions with `keepCount=2` → prune runs → for each of the 2 pruned versions, the 3 thumbnail blobs are deleted via `IStorageProvider.DeleteAsync`, and the cascade removes the rows when the `FileVersion` row is deleted.
- **TC-005**: Race test (best-effort) — start a generation consumer for a `FileVersion` that is soft-deleted mid-flight; verify generation no-ops gracefully (returns without error after seeing the row hidden).

## Implementation Tasks

- [ ] Add `ThumbnailCleanupConsumer`.
- [ ] Register in `Program.cs`: `busCfg.AddConsumer<ThumbnailCleanupConsumer>()`.
- [ ] Modify `FileVersionStore.PruneVersionsAsync` per the diff above.
- [ ] Integration tests under `tests/Strg.Integration.Tests/Thumbnails/ThumbnailCleanupTests.cs`.

## Security Review Checklist

- [ ] `StoragePath.Parse` wraps every thumbnail key before `IStorageProvider.DeleteAsync`.
- [ ] Tenant scope: cleanup consumer reads tenant from `FileDeletedEvent.TenantId`.
- [ ] No `IgnoreQueryFilters` — soft-deleted rows are excluded from `GetByFileAsync` by the global filter (which is the desired idempotency behavior).
- [ ] Best-effort blob delete failures are logged but never expose user paths in the warning message (`StorageKey` is opaque, but verify it doesn't include user-controlled segments).

## Code Review Checklist

- [ ] Prune-loop change is **additive** — existing transaction scope and retry semantics preserved.
- [ ] `ConfigureAwait(false)` consistent with the surrounding file's style.
- [ ] No N+1 — `ThumbnailEntries` query happens once per version (acceptable; alternative is a join, but the surrounding loop is per-version anyway).
- [ ] Dead-letter behavior: cleanup consumer should NOT dead-letter on best-effort blob errors (the catch makes this safe).

## Definition of Done

- [ ] All acceptance criteria green.
- [ ] Integration tests pass: TC-001 through TC-005 each have named test methods.
- [ ] Manually verified: pruned `FileVersion` leaves no orphan thumbnail blobs in `InMemoryStorageProvider` after the test runs.
