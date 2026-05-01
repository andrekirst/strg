---
id: STRG-342
title: Admin-triggered backfill + ThumbnailGenerationRequestedEvent
milestone: v0.2
priority: medium
status: open
type: feature
labels: [thumbnails, phase-15, admin, backfill, graphql]
depends_on: [STRG-331, STRG-340]
blocks: [STRG-343]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-342: Admin-triggered backfill + ThumbnailGenerationRequestedEvent

## Summary

Admin-only GraphQL mutation `regenerateThumbnails(driveId: ID, olderThan: DateTime)` that enumerates `FileVersion` rows lacking a `Ready` or `Unsupported` thumbnail entry and publishes a dedicated `ThumbnailGenerationRequestedEvent` per file to the outbox. The generation consumer (STRG-331) already handles both `FileUploadedEvent` AND `ThumbnailGenerationRequestedEvent` via a shared private method.

## Background / Context

Two scenarios drive this feature:

1. **Phase 16 retrofit** — when STRG-345 (PDF) ships, every existing PDF in storage needs thumbnails. Backfill must work for new MIME types without re-deploying the consumer.
2. **Algorithm bumps** — `ThumbnailEntry.GeneratorVersion` (STRG-329) tracks which generator produced a row. A future generator-version bump (e.g., new EXIF strip rules) requires a forced re-gen.

Issue #52 explicitly chose **dedicated event** over **republished `FileUploadedEvent`**: republishing would double-write `AuditEntry` rows via `AuditLogConsumer`. The dedicated event is consumed by `ThumbnailGenerationConsumer` only.

## Technical Specification

### Mutation — `src/Strg.GraphQl/Mutations/RegenerateThumbnailsMutation.cs`

```csharp
[ExtendObjectType("Mutation")]
public sealed class RegenerateThumbnailsMutation
{
    [Authorize(Roles = new[] { "admin" })]
    public async Task<RegenerateThumbnailsPayload> RegenerateThumbnails(
        Guid? driveId,
        DateTime? olderThan,
        [Service] StrgDbContext db,
        [Service] IPublishEndpoint bus,
        [Service] ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var query = db.FileVersions.AsQueryable();

        if (driveId is { } d)
        {
            query = from v in query
                    join f in db.Files on v.FileId equals f.Id
                    where f.DriveId == d
                    select v;
        }

        if (olderThan is { } cutoff)
        {
            query = query.Where(v => v.CreatedAt < cutoff);
        }

        // Versions without ANY Ready/Unsupported row across all variants.
        // (We don't filter per-variant — the consumer decides which variants need work.)
        query = query.Where(v => !db.ThumbnailEntries.Any(t =>
            t.FileVersionId == v.Id
            && (t.Status == ThumbnailStatus.Ready || t.Status == ThumbnailStatus.Unsupported)));

        var candidates = await query
            .Select(v => new { v.FileId, v.Id, v.File.DriveId })
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var c in candidates)
        {
            await bus.Publish(
                new ThumbnailGenerationRequestedEvent(
                    tenant.TenantId, c.FileId, c.Id, c.DriveId),
                cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);   // commits outbox events

        return new RegenerateThumbnailsPayload(candidates.Count);
    }
}

public sealed record RegenerateThumbnailsPayload(int FilesQueued);
```

### Consumer extension — STRG-331 update

`ThumbnailGenerationConsumer` already implements `IConsumer<ThumbnailGenerationRequestedEvent>` (per STRG-331's spec). The shared `ProcessAsync` method handles both event types identically. No change required here.

### Why "Files queued" not "Files processed"

The mutation returns immediately after staging outbox events — the actual generation is async. Returning `FilesQueued` (count) sets correct expectations. The admin UI polls with `inboxFiles` or refreshes the affected files; the GraphQL subscription `thumbnailReady` fires for each.

### Pagination / batching

For a large drive (10K+ files), the mutation returns a list of all candidates and stages all events in a single transaction. This is fine for v1 since the outbox dispatches lazily and the consumer's MassTransit pipeline rate-limits via `concurrentMessageLimit` (default per-process). If admins start backfilling million-file drives, follow-up work would chunk the publish into smaller transactions — flagged in STRG-344 / future issue.

### Auth — admin-only

`[Authorize(Roles = new[] { "admin" })]` honours the existing role policy (STRG-013 wires JWT role claims). Non-admin callers get a GraphQL error mapped from the auth pipeline.

## Acceptance Criteria

- [ ] Mutation `regenerateThumbnails` exists, admin-only (`[Authorize(Roles = ["admin"])]`).
- [ ] Optional `driveId` and `olderThan` arguments filter the candidate set.
- [ ] Candidate set is `FileVersion` rows WITHOUT a `Ready` OR `Unsupported` thumbnail entry across all variants.
- [ ] One `ThumbnailGenerationRequestedEvent` published per candidate file.
- [ ] Returns `RegenerateThumbnailsPayload(filesQueued: Int!)`.
- [ ] No `AuditEntry` row produced by the backfill (the consumer is the only handler of `ThumbnailGenerationRequestedEvent`; `AuditLogConsumer` does not subscribe to it).
- [ ] Generation consumer treats backfill events identically to `FileUploadedEvent` (same idempotency, same metrics).

## Test Cases

- **TC-001**: Drive with 10 files (none have thumbnails) → mutation returns `filesQueued: 10` → consumer fires for each → all 30 thumbnail rows reach `Ready` (10 files × 3 variants).
- **TC-002**: Drive with 5 ready + 5 unsupported (encrypted-drive carve-out) + 3 missing files → mutation returns `filesQueued: 3` (skips ready and unsupported).
- **TC-003**: `olderThan` = `1 hour ago` → only files older than 1 hour are queued.
- **TC-004**: Non-admin caller → mutation returns auth error (no events published).
- **TC-005**: No `AuditEntry` row appears for the queued events (verified by query against `AuditEntries` after the test) — only the consumer's own audit (if any) writes audit rows, and the consumer doesn't write audit rows for backfill.
- **TC-006**: Re-run the mutation → already-`Ready` files are skipped → `filesQueued: 0` → no duplicate work.

## Implementation Tasks

- [ ] Add `RegenerateThumbnailsMutation` extending `Mutation` type.
- [ ] Add `RegenerateThumbnailsPayload` record.
- [ ] Verify `ThumbnailGenerationConsumer` already handles `ThumbnailGenerationRequestedEvent` (STRG-331).
- [ ] Verify `AuditLogConsumer` does NOT subscribe to `ThumbnailGenerationRequestedEvent`.
- [ ] Tests under `tests/Strg.Integration.Tests/Thumbnails/ThumbnailBackfillTests.cs`.

## Security Review Checklist

- [ ] Admin-role gate enforced (`[Authorize(Roles = ["admin"])]`).
- [ ] Tenant scoping: candidate query inherits the tenant filter (no `IgnoreQueryFilters`).
- [ ] `driveId` argument validated to belong to the caller's tenant (the EF tenant filter handles this; cross-tenant `driveId` produces an empty candidate set, not an error).
- [ ] `olderThan` is bounded by the EF query — no raw SQL.
- [ ] Backfill cannot be used to mass-trigger CPU-heavy generation as a DoS vector against the operator (it's admin-only; the only attacker who can call it is a compromised admin account, in which case other access is the bigger concern).

## Code Review Checklist

- [ ] `[Authorize]` attribute on the mutation method.
- [ ] EF query is composable (`AsQueryable()`); no premature `ToList()`.
- [ ] No `Distinct()` on a non-keyed projection — the `.Distinct()` here projects unique `(FileId, Id, DriveId)` triples which is fine.
- [ ] No N+1 — single bulk publish loop.

## Definition of Done

- [ ] All acceptance criteria green.
- [ ] Tests pass.
- [ ] Manual smoke: as admin, run `mutation { regenerateThumbnails(driveId: $id) { filesQueued } }`, observe events fired and rows reach `Ready`.
