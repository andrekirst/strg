---
id: STRG-031
title: Create FileItem and FileVersion domain entities
milestone: v0.1
priority: critical
status: done
type: implementation
labels: [core, domain, files]
depends_on: [STRG-003, STRG-025]
blocks: [STRG-032, STRG-034, STRG-044]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-031: Create FileItem and FileVersion domain entities

## Summary

Create `FileItem` (a file or folder in a drive) and `FileVersion` (an immutable blob snapshot at a point in time) in `Strg.Core.Domain`.

## Technical Specification

### `src/Strg.Core/Domain/FileItem.cs`

```csharp
public sealed class FileItem : TenantedEntity
{
    public Guid DriveId { get; init; }
    public Guid? ParentId { get; set; }
    public required string Name { get; set; }
    public required string Path { get; set; }  // materialized path: "docs/2024/report.pdf" — separator '/', max 2048 chars
    public long Size { get; set; }
    public string? ContentHash { get; set; }   // SHA-256 hex
    public bool IsDirectory { get; init; }
    public Guid CreatedBy { get; init; }
    public string MimeType { get; set; } = "application/octet-stream";
    public int VersionCount { get; set; } = 1;
}
```

### `src/Strg.Core/Domain/FileVersion.cs`

```csharp
public sealed class FileVersion : Entity
{
    public Guid FileId { get; init; }
    public int VersionNumber { get; init; }
    public long Size { get; init; }
    public required string ContentHash { get; init; }
    public required string StorageKey { get; init; }   // opaque path in storage backend (DEK stored separately in file_keys table)
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public Guid CreatedBy { get; init; }
}
```

### `src/Strg.Core/Domain/IFileRepository.cs`

```csharp
public interface IFileRepository
{
    Task<FileItem?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<FileItem?> GetByPathAsync(Guid driveId, string path, CancellationToken ct = default);
    Task<IReadOnlyList<FileItem>> ListByParentAsync(Guid driveId, Guid? parentId, CancellationToken ct = default);
    Task AddAsync(FileItem file, CancellationToken ct = default);
    Task UpdateAsync(FileItem file, CancellationToken ct = default);
    Task SoftDeleteAsync(Guid id, CancellationToken ct = default);
}
```

## Acceptance Criteria

- [ ] `FileItem` inherits `TenantedEntity`
- [ ] `FileVersion` inherits `Entity` (NOT `TenantedEntity` — versions are immutable and access-controlled via `FileItem`)
- [ ] `FileItem.Path` is the materialized path (full path from drive root, e.g., `"docs/report.pdf"`) — separator `/`, max 2048 characters, `HasMaxLength(2048)` in EF Core config
- [ ] `FileItem.ContentHash` is nullable (directories have no hash)
- [ ] `FileVersion.StorageKey` is the opaque backend path (not the same as `FileItem.Path`)
- [ ] `FileVersion` properties are all `init` (truly immutable)
- [ ] `IFileRepository` and `IFileVersionRepository` both in `Strg.Core.Domain`
- [ ] Zero new package dependencies in `Strg.Core`

## Test Cases

- **TC-001**: `FileItem { Name = "report.pdf", IsDirectory = false }` → correct defaults
- **TC-002**: Attempt to set `FileVersion.ContentHash` after initialization → compile error
- **TC-003**: `FileItem.IsDeleted` after `SoftDeleteAsync` → `true`

## Implementation Tasks

- [ ] Create `FileItem.cs`
- [ ] Create `FileVersion.cs`
- [ ] Create `IFileRepository.cs`
- [ ] Create `IFileVersionRepository.cs`
- [ ] Write basic unit tests for entity behavior

## Definition of Done

- [ ] All files created with correct properties
- [ ] Zero Strg.Core package additions
