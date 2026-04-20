---
id: STRG-039
title: Implement REST file delete endpoint (DELETE /drives/{driveId}/files/{fileId})
milestone: v0.1
priority: high
status: open
type: implementation
labels: [api, files, rest]
depends_on: [STRG-033, STRG-025, STRG-013, STRG-061]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-039: Implement REST file delete endpoint

## Summary

Implement `DELETE /api/v1/drives/{driveId}/files/{fileId}` that soft-deletes a `FileItem`. For directories, all children are recursively soft-deleted. A `FileDeletedEvent` is published via the MassTransit outbox.

## Technical Specification

### Route: `DELETE /api/v1/drives/{driveId}/files/{fileId}`

### Handler:

```csharp
private static async Task<IResult> DeleteFileAsync(
    Guid driveId,
    Guid fileId,
    [FromServices] IFileRepository repo,
    [FromServices] IPublishEndpoint publishEndpoint,
    ClaimsPrincipal user,
    CancellationToken ct)
{
    var file = await repo.GetByIdAsync(fileId, ct);
    if (file is null || file.DriveId != driveId) return Results.NotFound();

    file.DeletedAt = DateTimeOffset.UtcNow;
    file.IsDeleted = true;

    if (file.IsDirectory)
    {
        await foreach (var child in repo.GetDescendantsAsync(file.Path + "/", driveId))
        {
            child.DeletedAt = DateTimeOffset.UtcNow;
            child.IsDeleted = true;
        }
    }

    await repo.SaveChangesAsync(ct);

    await publishEndpoint.Publish(new FileDeletedEvent(
        TenantId: user.GetTenantId(),
        FileId: fileId,
        DriveId: driveId,
        UserId: user.GetUserId()), ct);

    return Results.NoContent();
}
```

### Key behaviors:

- Soft-delete only: sets `DeletedAt` and `IsDeleted = true`, does NOT remove physical file
- Physical cleanup is a separate background job (future)
- Recursive delete uses `repo.GetDescendantsAsync` (streaming, not load-all-into-memory)

## Acceptance Criteria

- [ ] `DELETE /api/v1/drives/{driveId}/files/{fileId}` → `204 No Content`
- [ ] Deleted file returns `404` on subsequent `GET`
- [ ] Deleting a directory → all children soft-deleted
- [ ] `FileDeletedEvent` published to outbox
- [ ] File in a different drive → `404` (not `403` — no information leakage)
- [ ] Requires `files.write` scope

## Test Cases

- **TC-001**: Delete file → 204, subsequent GET returns 404
- **TC-002**: Delete directory → all children marked `IsDeleted = true` in DB
- **TC-003**: Delete file from wrong drive → 404
- **TC-004**: `FileDeletedEvent` published after delete
- **TC-005**: Unauthenticated delete → 401

## Implementation Tasks

- [ ] Add `MapDelete` handler in `FileEndpoints.cs`
- [ ] Use `GetDescendantsAsync` for recursive soft-delete
- [ ] Publish `FileDeletedEvent` after `SaveChangesAsync`
- [ ] Write OpenAPI docs

## Testing Tasks

- [ ] Integration test: delete → 204, subsequent list excludes item
- [ ] Integration test: recursive delete of directory with children

## Security Review Checklist

- [ ] Wrong drive ID → 404 (not 403, to prevent enumeration)
- [ ] `files.write` scope required
- [ ] Physical file not deleted (no data loss risk in v0.1)

## Code Review Checklist

- [ ] Outbox event published after, not before, `SaveChangesAsync`
- [ ] `GetDescendantsAsync` uses `IAsyncEnumerable` (streaming)

## Definition of Done

- [ ] Soft-delete works for files and directories
- [ ] Outbox event published
