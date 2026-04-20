---
id: STRG-041
title: Implement REST file copy endpoint (POST /drives/{driveId}/files/{fileId}/copy)
milestone: v0.1
priority: high
status: open
type: implementation
labels: [api, files, rest]
depends_on: [STRG-033, STRG-024, STRG-061]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-041: Implement REST file copy endpoint

## Summary

Implement `POST /api/v1/drives/{driveId}/files/{fileId}/copy` that copies a file to a new path (optionally in a different drive). Creates a new `FileItem` and `FileVersion` in the DB. Fires `FileUploadedEvent` for the new file.

## Technical Specification

### Route: `POST /api/v1/drives/{driveId}/files/{fileId}/copy`

### Request body:

```json
{
  "targetPath": "copies/report-copy.pdf",
  "targetDriveId": null
}
```

### Handler:

```csharp
private static async Task<IResult> CopyFileAsync(
    Guid driveId,
    Guid fileId,
    CopyFileRequest request,
    [FromServices] IFileRepository repo,
    [FromServices] IDriveRepository driveRepo,
    [FromServices] IStorageProviderRegistry registry,
    [FromServices] IFileVersionRepository versionRepo,
    [FromServices] IQuotaService quotaService,
    [FromServices] IPublishEndpoint publishEndpoint,
    ClaimsPrincipal user,
    CancellationToken ct)
{
    var targetPath = StoragePath.Parse(request.TargetPath);

    var source = await repo.GetByIdAsync(fileId, ct);
    if (source is null || source.DriveId != driveId) return Results.NotFound();

    var targetDriveId = request.TargetDriveId ?? driveId;

    if (await repo.ExistsAsync(targetDriveId, targetPath.Value, ct))
        return Results.Conflict("Target path already exists.");

    await quotaService.CheckAsync(user.GetUserId(), source.Size, ct);

    var provider = registry.GetProvider(/* target drive */);
    await provider.CopyAsync(source.Path, targetPath.Value, ct);

    var newFile = new FileItem
    {
        Id = Guid.NewGuid(),
        DriveId = targetDriveId,
        TenantId = source.TenantId,
        Name = targetPath.FileName,
        Path = targetPath.Value,
        Size = source.Size,
        ContentHash = source.ContentHash,
        MimeType = source.MimeType,
        IsDirectory = false,
        CreatedBy = user.GetUserId()
    };

    var version = new FileVersion
    {
        FileId = newFile.Id,
        VersionNumber = 1,
        Size = source.Size,
        ContentHash = source.ContentHash,
        StorageKey = targetPath.Value,
        CreatedBy = user.GetUserId()
    };

    repo.Add(newFile);
    versionRepo.Add(version);
    await quotaService.CommitAsync(user.GetUserId(), source.Size, ct);
    await repo.SaveChangesAsync(ct);

    await publishEndpoint.Publish(new FileUploadedEvent(
        source.TenantId, newFile.Id, targetDriveId, user.GetUserId(), source.Size, source.MimeType), ct);

    return Results.CreatedAtRoute(/* ... */, FileItemDto.From(newFile));
}
```

## Acceptance Criteria

- [ ] `POST /copy` → `201 Created` with new `FileItem` (different `Id` from source)
- [ ] Copy to existing path → `409 Conflict`
- [ ] New `FileVersion` created for copied file (version 1)
- [ ] Quota checked before copy (source size counts against target user's quota)
- [ ] `FileUploadedEvent` published for the new file
- [ ] Original file unchanged

## Test Cases

- **TC-001**: Copy → new file has different `Id`, same content
- **TC-002**: Copy to existing path → `409`
- **TC-003**: Copy exceeds quota → `507 Insufficient Storage`
- **TC-004**: Original file unchanged after copy

## Implementation Tasks

- [ ] Add `MapPost("{fileId}/copy")` in `FileEndpoints.cs`
- [ ] Create `CopyFileRequest` record
- [ ] Create `FileVersion` for the new file
- [ ] Quota check and commit

## Testing Tasks

- [ ] Integration test: copy → new file accessible, original unchanged
- [ ] Integration test: copy exceeding quota → 507

## Security Review Checklist

- [ ] `targetPath` validated via `StoragePath.Parse()`
- [ ] Target drive ownership verified (user must own target drive)

## Code Review Checklist

- [ ] New `FileItem.Id` is a fresh `Guid.NewGuid()`, not the source ID
- [ ] `FileVersion.VersionNumber = 1` (copies start at version 1)

## Definition of Done

- [ ] Copy produces new `FileItem` with new `Id`
- [ ] Quota enforced
