---
id: STRG-024
title: Implement LocalFileSystemProvider
milestone: v0.1
priority: critical
status: open
type: implementation
labels: [infrastructure, storage]
depends_on: [STRG-021, STRG-022, STRG-023]
blocks: [STRG-025, STRG-034]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-024: Implement LocalFileSystemProvider

## Summary

Implement `IStorageProvider` backed by the local filesystem. This is the default built-in storage backend. The `BasePath` (root directory) is read from the drive's `ProviderConfig` JSON — each drive can have its own root path on the same machine.

## Technical Specification

### `ProviderConfig` schema for local drives:

```json
{
  "rootPath": "/var/strg/drives/my-drive"
}
```

`rootPath` is resolved at drive registration time and stored in `Drive.ProviderConfig`. The `StorageProviderRegistry` deserializes this JSON and passes `rootPath` to the provider constructor.

### File: `src/Strg.Infrastructure/Storage/LocalFileSystemProvider.cs`

```csharp
public class LocalFileSystemProvider(string basePath) : IStorageProvider
{
    // basePath comes from ProviderConfig["rootPath"] for this drive
    public string ProviderType => "local";

    public Task<IStorageFile?> GetFileAsync(string path, CancellationToken ct)
    {
        var fullPath = ResolvePath(path);
        if (!File.Exists(fullPath)) return Task.FromResult<IStorageFile?>(null);
        var info = new FileInfo(fullPath);
        return Task.FromResult<IStorageFile?>(new LocalStorageFile(path, info));
    }

    public Task<Stream> ReadAsync(string path, long offset, CancellationToken ct)
    {
        var fullPath = ResolvePath(path);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, useAsync: true);
        if (offset > 0) stream.Seek(offset, SeekOrigin.Begin);
        return Task.FromResult<Stream>(stream);
    }

    private string ResolvePath(string relativePath)
    {
        var parsed = StoragePath.Parse(relativePath);
        var full = Path.GetFullPath(Path.Combine(basePath, parsed.Value));
        // Verify resolved path is still under basePath (defense in depth)
        if (!full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            throw new StoragePathException("Path escapes base directory");
        return full;
    }

    // ... WriteAsync, DeleteAsync, MoveAsync, CopyAsync, ExistsAsync, CreateDirectoryAsync, ListAsync
}
```

### Performance requirements:
- `ReadAsync` uses async file streaming (no `File.ReadAllBytes`)
- `WriteAsync` streams to disk (no buffering entire content in memory)
- `ListAsync` yields entries lazily via `IAsyncEnumerable`

## Acceptance Criteria

- [ ] `ProviderType == "local"`
- [ ] `GetFileAsync` returns `null` when file not found (not throws)
- [ ] `ReadAsync` with `offset > 0` seeks correctly
- [ ] `WriteAsync` creates intermediate directories automatically
- [ ] `DeleteAsync` on non-existent path returns silently (idempotent)
- [ ] `MoveAsync` works across directories within the same `BasePath`
- [ ] `ListAsync` yields entries without loading all into memory
- [ ] All operations reject path traversal (via `StoragePath.Parse`)
- [ ] A resolved absolute path that escapes `BasePath` is rejected (defense in depth)
- [ ] `ReadAsync` returns a seekable stream with `CanRead == true`

## Test Cases

- **TC-001**: Write file → Read file → content matches
- **TC-002**: `GetFileAsync` on missing path → `null`
- **TC-003**: `ReadAsync` with `offset = 100` → first byte read is byte 100 of the file
- **TC-004**: `WriteAsync` with path `"sub/dir/file.txt"` → intermediate directories created
- **TC-005**: `DeleteAsync` on non-existent file → no exception
- **TC-006**: `ListAsync` on directory with 10,000 files → does not OOM (streaming)
- **TC-007**: Write to `"../escape.txt"` → `StoragePathException`
- **TC-008**: Move `"a/file.txt"` to `"b/file.txt"` → file moved, original gone

## Implementation Tasks

- [ ] Create `LocalFileSystemProvider.cs`
- [ ] Create `LocalStorageFile.cs` and `LocalStorageDirectory.cs` (IStorageFile/Directory impls)
- [ ] Implement all `IStorageProvider` methods
- [ ] Add defense-in-depth path escape check in `ResolvePath`
- [ ] Add `LocalProviderConfig` record for `ProviderConfig` deserialization (`rootPath`)
- [ ] Verify `StorageProviderRegistry.Resolve("local", config)` passes `rootPath` to constructor
- [ ] Write unit tests using temp directory
- [ ] Write performance test for large directory listing

## Security Review Checklist

- [ ] Double path traversal check: `StoragePath.Parse` + full path starts-with-base
- [ ] No `File.ReadAllBytes` or `File.ReadAllText` (memory exhaustion prevention)
- [ ] Symlinks not followed (use `FileInfo.LinkTarget` check)
- [ ] File permissions not elevated during operations (runs as application user)
- [ ] `WriteAsync` does not truncate existing files unexpectedly (use `FileMode.Create`)

## Code Review Checklist

- [ ] `FileStream` opened with `FileShare.Read` to allow concurrent reads
- [ ] `useAsync: true` on all `FileStream` constructors
- [ ] `IAsyncEnumerable` uses `yield return` (not `ToListAsync` first)

## Definition of Done

- [ ] All test cases pass
- [ ] Security review completed
- [ ] Works with `StorageProviderRegistry` (STRG-023)
