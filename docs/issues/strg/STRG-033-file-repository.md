---
id: STRG-033
title: Implement FileRepository and FileVersionRepository
milestone: v0.1
priority: critical
status: open
type: implementation
labels: [infrastructure, files, repository]
depends_on: [STRG-004, STRG-031]
blocks: [STRG-034, STRG-037, STRG-038, STRG-039, STRG-040, STRG-041]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-033: Implement FileRepository and FileVersionRepository

## Summary

Implement the `FileRepository` and `FileVersionRepository` infrastructure classes that back the `IFileRepository` and `IFileVersionRepository` interfaces from `Strg.Core`. All queries respect the EF Core global query filters (tenant isolation + soft-delete).

## Technical Specification

### File: `src/Strg.Infrastructure/Repositories/FileRepository.cs`

```csharp
public sealed class FileRepository : IFileRepository
{
    private readonly StrgDbContext _db;

    public FileRepository(StrgDbContext db) => _db = db;

    public Task<FileItem?> GetByIdAsync(Guid id, CancellationToken ct)
        => _db.Files.FirstOrDefaultAsync(f => f.Id == id, ct);

    public Task<FileItem?> GetByPathAsync(Guid driveId, string path, CancellationToken ct)
        => _db.Files.FirstOrDefaultAsync(f => f.DriveId == driveId && f.Path == path, ct);

    public IAsyncEnumerable<FileItem> GetChildrenAsync(Guid parentId)
        => _db.Files
              .Where(f => f.ParentId == parentId)
              .OrderBy(f => f.Name)
              .AsAsyncEnumerable();

    public IAsyncEnumerable<FileItem> GetDescendantsAsync(string pathPrefix, Guid driveId)
        => _db.Files
              .Where(f => f.DriveId == driveId && f.Path.StartsWith(pathPrefix + "/"))
              .AsAsyncEnumerable();

    public void Add(FileItem file) => _db.Files.Add(file);

    public void Remove(FileItem file) => _db.Files.Remove(file);

    public Task<bool> ExistsAsync(Guid driveId, string path, CancellationToken ct)
        => _db.Files.AnyAsync(f => f.DriveId == driveId && f.Path == path, ct);

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
```

### File: `src/Strg.Infrastructure/Repositories/FileVersionRepository.cs`

```csharp
public sealed class FileVersionRepository : IFileVersionRepository
{
    private readonly StrgDbContext _db;

    public FileVersionRepository(StrgDbContext db) => _db = db;

    public IAsyncEnumerable<FileVersion> GetVersionsAsync(Guid fileId)
        => _db.FileVersions
              .Where(v => v.FileId == fileId)
              .OrderByDescending(v => v.VersionNumber)
              .AsAsyncEnumerable();

    public Task<FileVersion?> GetVersionAsync(Guid fileId, int versionNumber, CancellationToken ct)
        => _db.FileVersions.FirstOrDefaultAsync(
               v => v.FileId == fileId && v.VersionNumber == versionNumber, ct);

    public Task<int> GetNextVersionNumberAsync(Guid fileId, CancellationToken ct)
        => _db.FileVersions
              .Where(v => v.FileId == fileId)
              .Select(v => v.VersionNumber)
              .DefaultIfEmpty(0)
              .MaxAsync(ct)
              .ContinueWith(t => t.Result + 1, ct);

    public void Add(FileVersion version) => _db.FileVersions.Add(version);
}
```

### DI registration (`src/Strg.Infrastructure/DependencyInjection.cs`):

```csharp
services.AddScoped<IFileRepository, FileRepository>();
services.AddScoped<IFileVersionRepository, FileVersionRepository>();
```

## Acceptance Criteria

- [ ] `GetByIdAsync` respects global query filter (no deleted or other-tenant files returned)
- [ ] `GetChildrenAsync` returns immediate children only (not descendants)
- [ ] `GetDescendantsAsync` returns all nested children (for recursive delete)
- [ ] `ExistsAsync` returns `true` only for non-deleted files
- [ ] `GetNextVersionNumberAsync` returns 1 for a file with no existing versions

## Test Cases

- **TC-001**: `GetByIdAsync` with deleted file → returns `null` (global filter)
- **TC-002**: `GetByIdAsync` with other tenant's file → returns `null`
- **TC-003**: `GetChildrenAsync` returns only direct children, not grandchildren
- **TC-004**: `GetNextVersionNumberAsync` for file with 3 versions → returns 4
- **TC-005**: `ExistsAsync` with soft-deleted file → returns `false`

## Implementation Tasks

- [ ] Create `FileRepository.cs` in `Strg.Infrastructure/Repositories/`
- [ ] Create `FileVersionRepository.cs` in `Strg.Infrastructure/Repositories/`
- [ ] Register both repositories in DI container
- [ ] Verify global query filters are active for all queries

## Testing Tasks

- [ ] Unit tests with `InMemoryDbContext` or SQLite in-memory
- [ ] Integration test: EF Core global filter prevents cross-tenant access

## Security Review Checklist

- [ ] No `IgnoreQueryFilters()` calls in repository (filters never bypassed)
- [ ] `GetDescendantsAsync` uses path prefix + `/` to prevent prefix collision

## Code Review Checklist

- [ ] `IAsyncEnumerable` used for streaming collections (not `List<T>`)
- [ ] Repository does not call `SaveChangesAsync` (caller responsibility)
- [ ] `FileRepository` is `sealed`

## Definition of Done

- [ ] Both repositories pass unit tests
- [ ] Global query filter verified in integration test
