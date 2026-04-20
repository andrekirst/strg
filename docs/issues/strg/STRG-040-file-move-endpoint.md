---
id: STRG-040
title: Implement REST file move endpoint (POST /drives/{driveId}/files/{fileId}/move)
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

# STRG-040: Implement REST file move endpoint

## Summary

Implement `POST /api/v1/drives/{driveId}/files/{fileId}/move` that moves a file or directory to a new path (optionally in a different drive). Updates the DB and calls the storage provider. Fires `FileMovedEvent`.

## Technical Specification

### Route: `POST /api/v1/drives/{driveId}/files/{fileId}/move`

### Request body:

```json
{
  "targetPath": "archive/2024/report.pdf",
  "targetDriveId": null
}
```

### Handler:

```csharp
private static async Task<IResult> MoveFileAsync(
    Guid driveId,
    Guid fileId,
    MoveFileRequest request,
    [FromServices] IFileRepository repo,
    [FromServices] IDriveRepository driveRepo,
    [FromServices] IStorageProviderRegistry registry,
    [FromServices] IPublishEndpoint publishEndpoint,
    ClaimsPrincipal user,
    CancellationToken ct)
{
    var targetPath = StoragePath.Parse(request.TargetPath); // throws StoragePathException

    var file = await repo.GetByIdAsync(fileId, ct);
    if (file is null || file.DriveId != driveId) return Results.NotFound();

    var targetDriveId = request.TargetDriveId ?? driveId;
    var targetDrive = await driveRepo.GetByIdAsync(targetDriveId, ct);
    if (targetDrive is null) return Results.NotFound();

    if (await repo.ExistsAsync(targetDriveId, targetPath.Value, ct))
        return Results.Conflict("Target path already exists.");

    var oldPath = file.Path;
    var provider = registry.GetProvider(targetDrive.ProviderType, targetDrive.ProviderConfig);
    await provider.MoveAsync(file.Path, targetPath.Value, ct);

    file.Path = targetPath.Value;
    file.Name = targetPath.FileName;
    file.DriveId = targetDriveId;

    await repo.SaveChangesAsync(ct);

    await publishEndpoint.Publish(new FileMovedEvent(
        user.GetTenantId(), fileId, targetDriveId, oldPath, targetPath.Value, user.GetUserId()), ct);

    return Results.Ok(FileItemDto.From(file));
}
```

## Acceptance Criteria

- [ ] `POST /moves` with valid `targetPath` → `200 OK` with updated `FileItem`
- [ ] File moved to existing path → `409 Conflict`
- [ ] Path with traversal (`../`) → `400 Bad Request` (`StoragePathException`)
- [ ] Cross-drive move (different `targetDriveId`) → file accessible at new drive/path
- [ ] `FileMovedEvent` published after move
- [ ] Original path returns `404` after move

## Test Cases

- **TC-001**: Move file → 200, new path accessible, old path 404
- **TC-002**: Move to existing path → 409
- **TC-003**: Cross-drive move → file in new drive
- **TC-004**: `StoragePathException` from `targetPath` → 400

## Implementation Tasks

- [ ] Add `MapPost("{fileId}/move")` in `FileEndpoints.cs`
- [ ] Create `MoveFileRequest` record
- [ ] Handle cross-drive storage provider selection
- [ ] Publish `FileMovedEvent` after save

## Testing Tasks

- [ ] Integration test: move → original path returns 404, new path returns 200
- [ ] Integration test: move to occupied path → 409

## Security Review Checklist

- [ ] `targetPath` validated via `StoragePath.Parse()`
- [ ] Target drive ownership verified

## Code Review Checklist

- [ ] Storage move called before DB update (rollback easier if DB fails)
- [ ] Actually: reverse — DB update in transaction, compensate storage if DB fails

## Definition of Done

- [ ] Move works within and across drives
- [ ] Path collision detected
