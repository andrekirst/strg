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
1. On `CREATE` (POST): validates JWT, **reserves quota atomically** via `IQuotaService.ReserveAsync()`, records upload in `pending_uploads` table
2. On `WRITE` (chunk): writes chunk bytes to `IStorageProvider` at a temp path
3. On `COMPLETE`: moves temp path to final path, creates `FileItem` + `FileVersion`, **commits quota reservation**, fires outbox event
4. On `ABORT` (DELETE or expired): releases quota reservation, cleans up temp file (handled by abandoned upload cleanup job — see STRG-XXX)

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
- [ ] `POST /upload` checks and **reserves** quota atomically — if quota exceeded → `413 Payload Too Large`
- [ ] `PATCH /upload/{id}` with correct `Upload-Offset` → chunk accepted
- [ ] `PATCH /upload/{id}` with wrong `Upload-Offset` → `409 Conflict`
- [ ] `HEAD /upload/{id}` → returns current `Upload-Offset` (resume point)
- [ ] Upload completes → `FileItem` created in database
- [ ] Upload completes → `file.uploaded` outbox event written in same transaction
- [ ] Upload aborted (`DELETE /upload/{id}`) → temp file cleaned up, quota released
- [ ] Concurrent chunks for same upload handled correctly

## Test Cases

- **TC-001**: Single-chunk upload of 1MB file → `FileItem` exists after completion
- **TC-002**: Simulated disconnect mid-upload → resume from last offset → completes successfully
- **TC-003**: Quota exceeded on chunk upload → `413`
- **TC-004**: Upload with invalid path (traversal) → `422`
- **TC-005**: Two concurrent uploads from same user within quota → both complete
- **TC-006**: Abort upload → `UsedBytes` not incremented
- **TC-007**: `HEAD` on unknown upload ID → `404`
- **TC-008**: `PATCH` with `Content-Length` mismatch → `400`

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
