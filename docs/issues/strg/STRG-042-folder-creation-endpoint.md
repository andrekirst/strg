---
id: STRG-042
title: Implement REST folder creation endpoint (POST /drives/{driveId}/folders)
milestone: v0.1
priority: high
status: open
type: implementation
labels: [api, files, rest]
depends_on: [STRG-033, STRG-025, STRG-013]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-042: Implement REST folder creation endpoint

## Summary

Implement `POST /api/v1/drives/{driveId}/folders` that creates a directory `FileItem` in the DB. No physical directory is created in the storage backend — strg uses virtual paths. Parent directories are auto-created if they don't exist.

## Technical Specification

### Route: `POST /api/v1/drives/{driveId}/folders`

### Request body:

```json
{
  "path": "docs/2024/reports"
}
```

### Handler (auto-creates parent segments):

```csharp
private static async Task<IResult> CreateFolderAsync(
    Guid driveId,
    CreateFolderRequest request,
    [FromServices] IFileRepository repo,
    ClaimsPrincipal user,
    CancellationToken ct)
{
    var path = StoragePath.Parse(request.Path);
    var tenantId = user.GetTenantId();
    var userId = user.GetUserId();

    // Ensure all parent segments exist
    var segments = path.Segments; // ["docs", "2024", "reports"]
    string currentPath = "";
    FileItem? parent = null;

    foreach (var segment in segments)
    {
        currentPath = currentPath == "" ? segment : $"{currentPath}/{segment}";

        var existing = await repo.GetByPathAsync(driveId, currentPath, ct);
        if (existing is null)
        {
            var dir = new FileItem
            {
                DriveId = driveId,
                TenantId = tenantId,
                ParentId = parent?.Id,
                Name = segment,
                Path = currentPath,
                IsDirectory = true,
                Size = 0,
                CreatedBy = userId
            };
            repo.Add(dir);
            await repo.SaveChangesAsync(ct);
            parent = dir;
        }
        else if (!existing.IsDirectory)
        {
            return Results.Conflict($"Path segment '{currentPath}' exists as a file.");
        }
        else
        {
            parent = existing;
        }
    }

    return Results.Ok(FileItemDto.From(parent!));
}
```

## Acceptance Criteria

- [ ] `POST /folders` with `path: "docs/2024"` → `200 OK` with folder `FileItem`
- [ ] Parent path segments auto-created (`docs/` created if it doesn't exist)
- [ ] Folder already exists → `200 OK` (idempotent, returns existing folder)
- [ ] Path segment collides with existing file → `409 Conflict`
- [ ] Path with traversal → `400 Bad Request`
- [ ] Requires `files.write` scope

## Test Cases

- **TC-001**: `POST path="a/b/c"` with no existing parents → `a`, `a/b`, `a/b/c` all created
- **TC-002**: `POST path="docs"` when `docs` already exists → 200, no duplicate created
- **TC-003**: `POST path="file.txt/subdir"` where `file.txt` is a file → 409
- **TC-004**: `POST path="../etc"` → 400 (`StoragePathException`)

## Implementation Tasks

- [ ] Add `MapPost("/")` in folder-specific group within `FileEndpoints.cs`
- [ ] Create `CreateFolderRequest` record
- [ ] Implement auto-create parent logic with loop

## Testing Tasks

- [ ] Integration test: nested path creation in single request
- [ ] Integration test: idempotent — same path twice → no error, no duplicate

## Security Review Checklist

- [ ] Path validated via `StoragePath.Parse()`
- [ ] No physical directory created in storage backend (DB only)

## Code Review Checklist

- [ ] `SaveChangesAsync` called once per segment (not batched, to get IDs)
- [ ] Each new `FileItem` has `ParentId` set to the previous segment's ID

## Definition of Done

- [ ] Nested folder creation works with auto-created parents
- [ ] Idempotent
