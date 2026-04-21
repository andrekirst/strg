---
id: STRG-034
title: Configure tusdotnet TUS upload endpoint with IStorageProvider-backed store
milestone: v0.1
priority: critical
status: open
type: implementation
labels: [api, files, upload]
depends_on: [STRG-024, STRG-025, STRG-031, STRG-032]
blocks: [STRG-035, STRG-036]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: large
---

# STRG-034: Configure tusdotnet TUS upload endpoint with IStorageProvider-backed store

## Summary

Configure `tusdotnet` middleware for the TUS resumable upload protocol. Implement a custom `ITusStore` backed by `IStorageProvider` so uploads go directly to the configured drive backend.

## Background

TUS protocol enables resumable file uploads. The client initiates with `POST /upload`, uploads chunks with `PATCH /upload/{id}`, and can resume after disconnection using `HEAD /upload/{id}`. This is the only supported upload mechanism in strg.

## Technical Specification

### Package: `tusdotnet`

### Endpoint: `PATCH /upload/{uploadId}` (and POST, HEAD, DELETE)

### File: `src/Strg.Infrastructure/Upload/StrgTusStore.cs`

Implements `ITusStore`, `ITusCreationStore`, `ITusReadableStore`, `ITusTerminationStore`.

The store:
1. On `CREATE` (POST): validates JWT, records upload in `pending_uploads` table. Quota is NOT reserved here — see STRG-032 (Commit-as-reservation semantics): `IQuotaService.CommitAsync` IS the reservation.
2. On `WRITE` (chunk): writes chunk bytes via `IEncryptingFileWriter.WriteAsync` to a **temp-namespaced** storage key (e.g., `uploads/temp/{driveId}/{ulid}`). The returned `WrappedDek` + `Algorithm` are staged in memory alongside the upload, NOT yet persisted.
3. On `COMPLETE`: opens a DB transaction, in this order — (a) stages `FileItem` + `FileVersion` (with the *final* storage key), (b) stages `FileKey` row with `WrappedDek` and `Algorithm = AesGcmFileWriter.AlgorithmName` from step 2, (c) calls `IQuotaService.CommitAsync` (this IS the reservation — see STRG-032), (d) `SaveChangesAsync` + `CommitAsync` on the DB tx, (e) **only after DB commit succeeds**, promotes temp key → final key via `IStorageProvider.MoveAsync`, (f) publishes `file.uploaded` via outbox. On DB-tx failure at (d) or earlier, the temp blob is deleted via `IStorageProvider.DeleteAsync` (best-effort, idempotent per the provider contract).
4. On `ABORT` (DELETE or expired): deletes temp file via `IStorageProvider.DeleteAsync` (idempotent). No quota release needed because nothing was reserved at CREATE.

### Two-Phase Upload Protocol (STRG-026 #2)

This endpoint is the production caller named in the `IEncryptingFileWriter` contract. The **strict ordering** of step 3 above closes the orphan-ciphertext gap captured in STRG-026 #2: a blob is never reachable at its final storage key until the corresponding `FileVersion` + `FileKey` rows are durably committed. Key invariants:

- **Temp namespace is mandatory.** The writer MUST be invoked at `uploads/temp/{driveId}/{ulid}`, never directly at the final key. A one-shot write-to-final would reintroduce the orphan gap: a DB failure after the blob is durable leaves unreachable ciphertext at a key that nothing will ever scan.
- **Promote via `IStorageProvider.MoveAsync` only after DB commit.** The move is a rename on local FS (atomic) and a copy-then-delete on S3 (non-atomic but idempotent). A post-commit MoveAsync failure leaves the DB pointing at a final key the blob hasn't reached yet — this is a **phase-3 inversion** and requires a temp-key backstop sweep (see below); it is strictly preferable to the pre-commit write-to-final gap, which would silently leak.
- **Temp-key backstop sweep.** The abandoned-upload cleanup job (STRG-036) MUST also sweep `uploads/temp/{driveId}/**` entries older than the upload TTL and unreferenced by any `pending_uploads` row. This catches phase-3 cleanup flakes (post-commit MoveAsync failure; DELETE-on-abort failure).
- **FileKey.Algorithm round-trip.** The write path persists `FileKey.Algorithm = AesGcmFileWriter.AlgorithmName`. The read path (download endpoint) MUST load `FileKey.Algorithm` from the row and pass it verbatim to `IEncryptingFileReader.ReadAsync` — this is the v0.2 dispatch hook; a hardcoded algorithm here would force a schema migration when alternate ciphers land.

The test suite's `NaiveEncryptedUploadService` (under `tests/Strg.Integration.Tests/Upload/`) is deliberately NOT two-phase and is pinned by the regression gate `Upload_failure_on_quota_orphans_ciphertext_blob_TODO_STRG034`. When this endpoint lands, that gate's polarity MUST flip from "orphan exists" (`.BeTrue()`) to "no orphan" (`.BeFalse()`), and the naive service should be retired.

### Registration:

```csharp
app.UseTus(ctx =>
{
    // Extract driveId from path: POST /upload?driveId={id}&path={relativePath}
    var driveId = Guid.Parse(ctx.Request.Query["driveId"]);
    var targetPath = ctx.Request.Query["path"].ToString();

    return new DefaultTusConfiguration
    {
        Store = ctx.RequestServices.GetRequiredService<StrgTusStoreFactory>()
                    .Create(driveId, targetPath),
        MaxAllowedUploadSizeInBytes = null,  // enforced by quota check
        Events = new Events
        {
            OnBeforeCreateAsync = ctx => ValidateRequest(ctx),
            OnFileCompleteAsync = ctx => CompleteUpload(ctx)
        }
    };
});
```

### Upload metadata (TUS metadata header):

```
Upload-Metadata: filename <base64>, content-type <base64>, drive-id <base64>, path <base64>
```

## Acceptance Criteria

- [ ] `POST /upload` with valid JWT and quota available → `201 Created` with `Location` header
- [ ] `POST /upload` without JWT → `401`
- [ ] `POST /upload` when pre-quota-check would reject (e.g., declared `Upload-Length` > remaining quota) → `413 Payload Too Large` (Commit-as-reservation means actual reservation happens at COMPLETE; the CREATE check is an early-rejection optimization only)
- [ ] `PATCH /upload/{id}` with correct `Upload-Offset` → chunk accepted
- [ ] `PATCH /upload/{id}` with wrong `Upload-Offset` → `409 Conflict`
- [ ] `HEAD /upload/{id}` → returns current `Upload-Offset` (resume point)
- [ ] Chunks are written to a temp-namespaced storage key (`uploads/temp/{driveId}/{ulid}`), never directly to the final key
- [ ] Upload completes → `FileItem` + `FileVersion` + `FileKey` (with `Algorithm` populated) committed in a single DB transaction
- [ ] Upload completes → promote-on-commit via `IStorageProvider.MoveAsync` runs **after** DB commit, never before
- [ ] DB transaction failure at COMPLETE → temp blob deleted via `IStorageProvider.DeleteAsync` (idempotent); no final-key blob exists
- [ ] Upload completes → `file.uploaded` outbox event written in same transaction
- [ ] Upload aborted (`DELETE /upload/{id}`) → temp file cleaned up
- [ ] Download path loads `FileKey.Algorithm` and passes it to `IEncryptingFileReader.ReadAsync` — a row with `Algorithm = "aes-256-gcm"` decrypts, a mismatched value rejects with `NotSupportedException`
- [ ] Concurrent chunks for same upload handled correctly
- [ ] Regression gate `Upload_failure_on_quota_orphans_ciphertext_blob_TODO_STRG034` flipped: after this endpoint lands, the orphan no longer exists and the gate asserts `.BeFalse()` (not `.BeTrue()`)

## Test Cases

- **TC-001**: Single-chunk upload of 1MB file → `FileItem` exists after completion
- **TC-002**: Simulated disconnect mid-upload → resume from last offset → completes successfully
- **TC-003**: Quota exceeded on commit → `413`, no `FileItem` / `FileVersion` / `FileKey` rows exist, no blob exists at the final key, temp blob cleaned up (this is the regression pin that STRG-026 #2's orphan gate flips)
- **TC-004**: Upload with invalid path (traversal) → `422`
- **TC-005**: Two concurrent uploads from same user within quota → both complete
- **TC-006**: Abort upload → `UsedBytes` not incremented
- **TC-007**: `HEAD` on unknown upload ID → `404`
- **TC-008**: `PATCH` with `Content-Length` mismatch → `400`
- **TC-009**: Successful upload → `FileKey` row has `Algorithm = "aes-256-gcm"` and a non-empty `WrappedDek`; subsequent download decrypts via that stored algorithm
- **TC-010**: Simulated `IStorageProvider.MoveAsync` failure after DB commit → temp blob remains at temp key, final key is empty, temp-key backstop sweep (STRG-036) reaps it (phase-3 inversion path — accepted failure window, not the orphan gap)

## Implementation Tasks

- [ ] Install `tusdotnet` package
- [ ] Create `StrgTusStore.cs` implementing all required TUS store interfaces
- [ ] Create `StrgTusStoreFactory.cs`
- [ ] Create `pending_uploads` table (or use tusdotnet's file storage)
- [ ] Integrate quota check on upload creation
- [ ] Implement `OnFileCompleteAsync` handler (DB transaction + outbox event)
- [ ] Register TUS middleware in `Program.cs`
- [ ] Write integration tests using a TUS client library

## Security Review Checklist

- [ ] JWT validation happens on every chunk (not just creation)
- [ ] Path from metadata is validated with `StoragePath.Parse` before writing
- [ ] Quota enforcement happens per-chunk (not just on creation request)
- [ ] Upload ID is a cryptographically random UUID (not sequential)
- [ ] Upload size limit: quota check is the enforcement, no hardcoded max (quota = limit)
- [ ] Temp files cleaned up on abort or process crash (background cleanup job)

## Code Review Checklist

- [ ] `OnFileCompleteAsync` is atomic (single DB transaction)
- [ ] Chunk writes use async I/O
- [ ] Upload IDs are opaque (not guessable)

## Definition of Done

- [ ] End-to-end TUS upload test passes (initiate → chunk → complete)
- [ ] Resume test passes
- [ ] Quota enforcement verified
- [ ] Outbox event verified in database after completion
