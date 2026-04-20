---
id: STRG-045
title: Implement file version restore endpoint (POST /files/{fileId}/versions/{n}/restore)
milestone: v0.1
priority: medium
status: open
type: implementation
labels: [api, files, versioning, rest]
depends_on: [STRG-044, STRG-043]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-045: Implement file version restore endpoint

## Summary

Implement `POST /api/v1/drives/{driveId}/files/{fileId}/versions/{versionNumber}/restore` that restores a file to a previous version. The restore creates a NEW version (not an overwrite) with the old version's content, preserving the full version history.

## Technical Specification

### Route: `POST /api/v1/drives/{driveId}/files/{fileId}/versions/{versionNumber}/restore`

### Behavior:

Restoring version N:
1. Read the storage object at `versions[N].StorageKey`
2. Write it to a new storage key (same path, overwrite current)
3. Create `FileVersion` with `VersionNumber = current_max + 1`
4. Update `FileItem.Size`, `ContentHash`, `VersionCount`

This ensures: version history is never rewritten, only appended.

### Handler:

```csharp
private static async Task<IResult> RestoreVersionAsync(
    Guid driveId,
    Guid fileId,
    int versionNumber,
    [FromServices] IFileVersionStore versionStore,
    [FromServices] IFileRepository fileRepo,
    [FromServices] IStorageProviderRegistry registry,
    [FromServices] IPublishEndpoint publishEndpoint,
    ClaimsPrincipal user,
    CancellationToken ct)
{
    var file = await fileRepo.GetByIdAsync(fileId, ct);
    if (file is null || file.DriveId != driveId) return Results.NotFound();

    var targetVersion = await versionStore.GetVersionAsync(fileId, versionNumber, ct);
    if (targetVersion is null) return Results.NotFound($"Version {versionNumber} not found.");

    var provider = /* resolve from drive */;

    // Read old version content
    var stream = await provider.ReadAsync(targetVersion.StorageKey, ct);

    // Write to current storage path (overwrites current version's bytes)
    await provider.WriteAsync(file.Path, stream, ct);

    // Create new version record
    var newVersion = await versionStore.CreateVersionAsync(
        file,
        storageKey: file.Path,
        contentHash: targetVersion.ContentHash,
        size: targetVersion.Size,
        createdBy: user.GetUserId(),
        ct);

    // Update FileItem metadata
    file.Size = targetVersion.Size;
    file.ContentHash = targetVersion.ContentHash;
    await fileRepo.SaveChangesAsync(ct);

    await publishEndpoint.Publish(new FileUploadedEvent(
        file.TenantId, file.Id, file.DriveId, user.GetUserId(), file.Size, file.MimeType), ct);

    return Results.Ok(FileItemDto.From(file));
}
```

## Acceptance Criteria

- [ ] `POST .../versions/2/restore` → file now serves version 2 content
- [ ] Restore creates a NEW version entry (version history not overwritten)
- [ ] Version number after restore = previous max + 1
- [ ] Restoring nonexistent version → `404`
- [ ] `FileUploadedEvent` published after restore (triggers audit log, search index)
- [ ] Requires `files.write` scope

## Test Cases

- **TC-001**: Upload v1, upload v2, restore v1 → file content matches v1, version count is 3
- **TC-002**: Restore version 99 → 404
- **TC-003**: `FileUploadedEvent` published after restore
- **TC-004**: Version history: 1, 2, 3(restored-from-1) — version 1 content accessible via all three

## Implementation Tasks

- [ ] Add `MapPost("{fileId}/versions/{versionNumber}/restore")` in `FileEndpoints.cs`
- [ ] Use `IFileVersionStore.CreateVersionAsync` to add new version record
- [ ] Publish `FileUploadedEvent`

## Testing Tasks

- [ ] Integration test: version restore cycle
- [ ] Integration test: original version still downloadable after restore

## Security Review Checklist

- [ ] Restore requires `files.write` scope
- [ ] Cannot restore a version from a different `fileId`

## Code Review Checklist

- [ ] Restore does not delete any version records
- [ ] New version number is `max + 1` (not hardcoded)

## Definition of Done

- [ ] Restore creates a new version with old content
- [ ] Version history preserved
