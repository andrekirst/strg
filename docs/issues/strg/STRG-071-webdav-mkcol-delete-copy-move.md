---
id: STRG-071
title: Implement WebDAV MKCOL, DELETE, COPY, MOVE handlers
milestone: v0.1
priority: high
status: open
type: implementation
labels: [webdav]
depends_on: [STRG-068]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-071: Implement WebDAV MKCOL, DELETE, COPY, MOVE handlers

## Summary

Implement the four file management WebDAV methods: `MKCOL` (create directory), `DELETE` (soft-delete), `COPY` (copy file/directory), and `MOVE` (move/rename). All operations sync to the DB and fire outbox events.

## Technical Specification

### MKCOL — Create Collection:

```csharp
public async Task<IWebDavStoreCollection> CreateCollectionAsync(
    string name, IHttpContext httpContext, CancellationToken ct)
{
    var path = StoragePath.Parse(_collectionPath).Combine(name);
    await _provider.CreateDirectoryAsync(path.Value, ct);

    var dirItem = new FileItem
    {
        DriveId = _drive.Id,
        TenantId = _drive.TenantId,
        Name = name,
        Path = path.Value,
        IsDirectory = true,
        Size = 0,
        CreatedBy = _userId
    };
    _db.Files.Add(dirItem);
    await _db.SaveChangesAsync(ct);

    return new StrgWebDavCollection(dirItem, _provider, _db, _store);
}
```

### DELETE — Soft delete:

```csharp
public async Task DeleteAsync(IHttpContext httpContext, CancellationToken ct)
{
    _fileItem.DeletedAt = DateTimeOffset.UtcNow;
    _fileItem.IsDeleted = true;

    if (_fileItem.IsDirectory)
    {
        // Recursively soft-delete children
        var children = await _db.Files
            .Where(f => f.Path.StartsWith(_fileItem.Path + "/") && !f.IsDeleted)
            .ToListAsync(ct);
        foreach (var child in children)
        {
            child.DeletedAt = DateTimeOffset.UtcNow;
            child.IsDeleted = true;
        }
    }

    await _db.SaveChangesAsync(ct);
    await _publishEndpoint.Publish(new FileDeletedEvent(
        _fileItem.TenantId, _fileItem.Id, _fileItem.DriveId, _userId), ct);
}
```

### COPY — Copy item:

- Creates new `FileItem` with new `Guid` ID
- Copies physical file in storage backend via `IStorageProvider.CopyAsync`
- `Overwrite: T` header → replace existing; `Overwrite: F` → `412 Precondition Failed` if exists

### MOVE — Move/Rename:

- Updates `FileItem.Path` and `FileItem.Name` in DB
- Calls `IStorageProvider.MoveAsync(oldPath, newPath)`
- `Destination` header parsed and validated via `StoragePath.Parse()`
- Fires `FileMovedEvent`

### Cross-drive operations:

COPY/MOVE across drives not supported in WebDAV (use REST API). If `Destination` host differs → `502 Bad Gateway`.

## Acceptance Criteria

- [ ] `MKCOL /dav/{drive}/newdir/` → `201 Created`, directory visible in subsequent PROPFIND
- [ ] `DELETE /dav/{drive}/file.txt` → `204 No Content`, file no longer in PROPFIND
- [ ] `DELETE /dav/{drive}/dir/` → `204`, all children soft-deleted
- [ ] `COPY /dav/{drive}/a.txt` `Destination: /dav/{drive}/b.txt` → `201 Created`
- [ ] `COPY` with `Overwrite: F` and existing destination → `412 Precondition Failed`
- [ ] `MOVE /dav/{drive}/a.txt` `Destination: /dav/{drive}/dir/a.txt` → `201`
- [ ] `MOVE` fires `FileMovedEvent` outbox event
- [ ] DELETE does NOT physically remove files from storage (soft-delete only)

## Test Cases

- **TC-001**: MKCOL → PROPFIND shows new directory
- **TC-002**: DELETE file → PROPFIND excludes it
- **TC-003**: DELETE directory with 3 children → all 4 items soft-deleted
- **TC-004**: COPY with `Overwrite: F` → destination exists → `412`
- **TC-005**: MOVE → original path returns 404, destination path returns 200

## Implementation Tasks

- [ ] Implement `CreateCollectionAsync` in `StrgWebDavCollection`
- [ ] Implement `DeleteAsync` in `StrgWebDavCollection` and `StrgWebDavDocument`
- [ ] Implement COPY (via `IStorageProvider.CopyAsync`)
- [ ] Implement MOVE (via `IStorageProvider.MoveAsync`)
- [ ] Parse `Destination` header and validate path
- [ ] Fire outbox events for DELETE and MOVE

## Testing Tasks

- [ ] Integration test: full MKCOL → PROPFIND → DELETE cycle
- [ ] Integration test: COPY then modify original → copy unchanged
- [ ] Integration test: MOVE → source gone, destination accessible
- [ ] Test: recursive soft-delete of nested directories

## Security Review Checklist

- [ ] `Destination` header validated through `StoragePath.Parse()`
- [ ] Cross-drive destination → rejected
- [ ] Recursive delete bounded (no circular reference follow)
- [ ] DELETE requires `files.write` scope

## Code Review Checklist

- [ ] Recursive soft-delete is a single DB batch UPDATE (not in-memory loop)
- [ ] `MOVE` is atomic: DB update + storage move in same transaction if possible
- [ ] `Path.StartsWith()` for recursive child matching uses `/` suffix (prevents prefix collision)

## Definition of Done

- [ ] macOS Finder and Windows Explorer can create/delete/rename folders and files
