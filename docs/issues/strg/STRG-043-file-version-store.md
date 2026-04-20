---
id: STRG-043
title: Implement FileVersionStore service
milestone: v0.1
priority: high
status: open
type: implementation
labels: [infrastructure, files, versioning]
depends_on: [STRG-033, STRG-031, STRG-024]
blocks: [STRG-044, STRG-045]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-043: Implement FileVersionStore service

## Summary

Implement `FileVersionStore` that manages file version lifecycle: creating version snapshots on upload, pruning old versions based on drive versioning policy, and providing version metadata. Implements `IFileVersionStore` from `Strg.Core`.

## Technical Specification

### Interface: `src/Strg.Core/Services/IFileVersionStore.cs`

```csharp
public interface IFileVersionStore
{
    Task<FileVersion> CreateVersionAsync(
        FileItem file,
        string storageKey,
        string contentHash,
        long size,
        Guid createdBy,
        CancellationToken ct);

    Task<IReadOnlyList<FileVersion>> GetVersionsAsync(Guid fileId, CancellationToken ct);

    Task<FileVersion?> GetVersionAsync(Guid fileId, int versionNumber, CancellationToken ct);

    Task PruneVersionsAsync(Guid fileId, int keepCount, CancellationToken ct);
}
```

### File: `src/Strg.Infrastructure/Versioning/FileVersionStore.cs`

```csharp
public sealed class FileVersionStore : IFileVersionStore
{
    private readonly IFileVersionRepository _versionRepo;
    private readonly IStorageProviderRegistry _registry;

    public async Task<FileVersion> CreateVersionAsync(
        FileItem file,
        string storageKey,
        string contentHash,
        long size,
        Guid createdBy,
        CancellationToken ct)
    {
        int nextNumber = await _versionRepo.GetNextVersionNumberAsync(file.Id, ct);

        var version = new FileVersion
        {
            FileId = file.Id,
            VersionNumber = nextNumber,
            Size = size,
            ContentHash = contentHash,
            StorageKey = storageKey,
            CreatedBy = createdBy
        };

        _versionRepo.Add(version);
        file.VersionCount = nextNumber;

        return version;
    }

    public async Task PruneVersionsAsync(Guid fileId, int keepCount, CancellationToken ct)
    {
        var versions = await _versionRepo.GetVersionsAsync(fileId)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync(ct);

        var toDelete = versions.Skip(keepCount).ToList();
        foreach (var version in toDelete)
        {
            // Delete physical storage object for this version
            var provider = ...; // resolve from drive
            await provider.DeleteAsync(version.StorageKey, ct);
            _versionRepo.Remove(version);
        }
    }
}
```

### Versioning policy (from `Drive.VersioningPolicy` JSON):

```json
{
  "keepVersions": 10,
  "enabled": true
}
```

- `keepVersions: 0` → all versions kept
- `keepVersions: 1` → only latest version kept (no history)
- `keepVersions: 10` → last 10 versions kept (older pruned after each upload)

## Acceptance Criteria

- [ ] `CreateVersionAsync` increments `VersionNumber` monotonically
- [ ] `FileItem.VersionCount` updated when version created
- [ ] `PruneVersionsAsync` deletes physical storage objects for pruned versions
- [ ] `PruneVersionsAsync(keepCount: 1)` keeps only the latest version
- [ ] `GetVersionsAsync` returns versions in descending version number order

## Test Cases

- **TC-001**: Upload 3 times with `keepVersions: 10` → 3 versions in DB
- **TC-002**: Upload with `keepVersions: 1` → only latest version remains
- **TC-003**: `GetVersionAsync(fileId, 2)` → returns version 2 data
- **TC-004**: `CreateVersionAsync` on file with 2 versions → new version is number 3

## Implementation Tasks

- [ ] Create `IFileVersionStore.cs` in `Strg.Core/Services/`
- [ ] Create `FileVersionStore.cs` in `Strg.Infrastructure/Versioning/`
- [ ] Register `IFileVersionStore` in DI
- [ ] Call `PruneVersionsAsync` from upload completion handler (STRG-034)

## Testing Tasks

- [ ] Unit test: pruning with `keepCount: 1` → oldest versions deleted
- [ ] Integration test: 5 uploads with `keepVersions: 3` → 3 versions in DB

## Security Review Checklist

- [ ] Pruning deletes only versions for the correct `fileId`
- [ ] `StorageKey` is an opaque internal path (never user-supplied)

## Code Review Checklist

- [ ] Pruning physical storage before DB remove (compensate on failure)
- [ ] `keepVersions: 0` means "keep all" (not "delete all")

## Definition of Done

- [ ] Version creation and pruning work end-to-end
- [ ] Integration test passes
