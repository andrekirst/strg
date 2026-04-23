---
id: STRG-068
title: Implement StrgWebDavStore (IWebDavStore bridge)
milestone: v0.1
priority: high
status: done
type: implementation
labels: [webdav, storage]
depends_on: [STRG-067, STRG-024, STRG-031]
blocks: [STRG-069, STRG-070, STRG-071, STRG-072]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: large
---

# STRG-068: Implement StrgWebDavStore (IWebDavStore bridge)

## Summary

Implement `StrgWebDavStore` that bridges the NWebDav `IWebDavStore` interface to strg's `IStorageProvider` abstraction. This is the central adapter that translates WebDAV collection/item concepts to strg `FileItem` and `Drive` entities.

## Technical Specification

### File: `src/Strg.WebDav/StrgWebDavStore.cs`

```csharp
public sealed class StrgWebDavStore : IWebDavStore
{
    private readonly IStorageProviderRegistry _registry;
    private readonly StrgDbContext _db;
    private readonly ILogger<StrgWebDavStore> _logger;

    public StrgWebDavStore(
        IStorageProviderRegistry registry,
        StrgDbContext db,
        ILogger<StrgWebDavStore> logger)
    {
        _registry = registry;
        _db = db;
        _logger = logger;
    }

    public async Task<IWebDavStoreItem> GetItemAsync(
        IWebDavStoreCollection collection,
        Uri uri,
        IPrincipal principal,
        CancellationToken ct)
    {
        var (driveId, path) = ParseUri(uri, principal);
        var drive = await ResolveDriveAsync(driveId, principal, ct);
        var provider = _registry.GetProvider(drive.ProviderType, drive.ProviderConfig);

        var fileItem = await _db.Files
            .FirstOrDefaultAsync(f => f.DriveId == drive.Id && f.Path == path && !f.IsDeleted, ct);

        if (fileItem == null) return null;

        return fileItem.IsDirectory
            ? new StrgWebDavCollection(fileItem, provider, _db, this)
            : new StrgWebDavDocument(fileItem, provider);
    }

    public Task<IWebDavStoreCollection> GetCollectionAsync(
        Uri uri, IPrincipal principal, CancellationToken ct)
        => /* resolve directory FileItem */ ...;
}
```

### `StrgWebDavCollection` and `StrgWebDavDocument`:

```csharp
// Collection (directory)
public sealed class StrgWebDavCollection : IWebDavStoreCollection
{
    public string Name => _fileItem.Name;
    public DateTime CreationDate => _fileItem.CreatedAt.DateTime;
    public DateTime LastModified => _fileItem.UpdatedAt.DateTime;
    public IEnumerable<IWebDavStoreItem> Items =>
        _db.Files
           .Where(f => f.ParentId == _fileItem.Id && !f.IsDeleted)
           .AsEnumerable()
           .Select(f => f.IsDirectory
               ? (IWebDavStoreItem)new StrgWebDavCollection(f, _provider, _db, _store)
               : new StrgWebDavDocument(f, _provider));
}

// Document (file)
public sealed class StrgWebDavDocument : IWebDavStoreDocument
{
    public string Name => _fileItem.Name;
    public long ContentLength => _fileItem.Size;
    public string ContentType => _fileItem.MimeType;
    public Task<Stream> GetReadableStreamAsync(CancellationToken ct)
        => _provider.ReadAsync(_fileItem.Path, ct);
}
```

### URI to path mapping:

- Request: `PROPFIND /dav/my-drive/docs/2024/report.pdf`
- Drive name: `my-drive` (from route value)
- Path: `docs/2024/report.pdf`
- Validated through `StoragePath.Parse()`

## Acceptance Criteria

- [ ] `PROPFIND /dav/{drive}/` → returns drive root collection with all top-level items
- [ ] `PROPFIND /dav/{drive}/subdir/` → returns directory listing
- [ ] `GET /dav/{drive}/file.txt` → streams file content
- [ ] Deleted files (soft-deleted) do NOT appear in WebDAV listing
- [ ] Path always validated through `StoragePath.Parse()` before storage calls
- [ ] Drive not accessible to user → `403 Forbidden`

## Test Cases

- **TC-001**: `PROPFIND /dav/test-drive/` → XML response with root items
- **TC-002**: `PROPFIND /dav/test-drive/` for soft-deleted file → item absent
- **TC-003**: `GET /dav/test-drive/file.txt` → 200 with file bytes
- **TC-004**: `PROPFIND /dav/test-drive/../etc/passwd` → `400 Bad Request` (path traversal rejected)
- **TC-005**: Drive owned by different tenant → `403`

## Implementation Tasks

- [ ] Create `StrgWebDavStore.cs` in `Strg.WebDav/`
- [ ] Create `StrgWebDavCollection.cs`
- [ ] Create `StrgWebDavDocument.cs`
- [ ] Create `WebDavUriParser` helper (URI → drive name + file path)
- [ ] Register `IStrgWebDavStore` in `AddStrgWebDav()`

## Testing Tasks

- [ ] Integration test: PROPFIND root collection → XML response
- [ ] Integration test: GET file → stream matches uploaded content
- [ ] Unit test: URI parser extracts drive name and path correctly
- [ ] Unit test: `StoragePath.Parse()` called before every storage operation

## Security Review Checklist

- [ ] Every path goes through `StoragePath.Parse()` before reaching `IStorageProvider`
- [ ] Drive ownership verified against current user's tenant
- [ ] Soft-deleted files excluded from listings (no information leak)

## Code Review Checklist

- [ ] `Items` in `StrgWebDavCollection` does not load all files into memory at once
- [ ] `StrgWebDavDocument.GetReadableStreamAsync` streams from provider, no `MemoryStream`
- [ ] `StrgWebDavStore` is `scoped`, not `singleton` (DbContext dependency)

## Definition of Done

- [ ] PROPFIND returns correct XML for both files and directories
- [ ] GET streams file content correctly
- [ ] Path traversal rejected
