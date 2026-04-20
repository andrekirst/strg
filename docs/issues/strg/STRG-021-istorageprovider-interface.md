---
id: STRG-021
title: Create IStorageProvider interface and storage primitives
milestone: v0.1
priority: critical
status: open
type: implementation
labels: [core, storage, interfaces]
depends_on: [STRG-003]
blocks: [STRG-022, STRG-023, STRG-024, STRG-025, STRG-028]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-021: Create IStorageProvider interface and storage primitives

## Summary

Define `IStorageProvider`, `IStorageFile`, `IStorageDirectory`, and `IStorageItem` in `Strg.Core.Storage`. These interfaces are the stable plugin contract that cannot change without a major version.

## Technical Specification

See full interface definitions in `docs/architecture/02-storage-abstraction.md`.

Key design decisions:
- `ReadAsync` accepts an `offset` parameter for range requests
- `ListAsync` returns `IAsyncEnumerable<IStorageItem>` for streaming large directories
- `GetFileAsync` returns null (not throws) when file not found
- All paths are relative to the drive root

### File: `src/Strg.Core/Storage/IStorageProvider.cs`

```csharp
namespace Strg.Core.Storage;

public interface IStorageProvider
{
    string ProviderType { get; }
    Task<IStorageFile?> GetFileAsync(string path, CancellationToken ct = default);
    Task<IStorageDirectory?> GetDirectoryAsync(string path, CancellationToken ct = default);
    Task<Stream> ReadAsync(string path, long offset = 0, CancellationToken ct = default);
    Task WriteAsync(string path, Stream content, CancellationToken ct = default);
    Task DeleteAsync(string path, CancellationToken ct = default);
    Task MoveAsync(string source, string destination, CancellationToken ct = default);
    Task CopyAsync(string source, string destination, CancellationToken ct = default);
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);
    Task CreateDirectoryAsync(string path, CancellationToken ct = default);
    IAsyncEnumerable<IStorageItem> ListAsync(string path, CancellationToken ct = default);
}
```

### File: `src/Strg.Core/Storage/IStorageItem.cs`

```csharp
public interface IStorageItem
{
    string Name { get; }
    string Path { get; }
    bool IsDirectory { get; }
    DateTimeOffset CreatedAt { get; }
    DateTimeOffset UpdatedAt { get; }
}

public interface IStorageFile : IStorageItem
{
    long Size { get; }
    string? ContentHash { get; }
}

public interface IStorageDirectory : IStorageItem { }
```

## Acceptance Criteria

- [ ] `IStorageProvider` in `Strg.Core.Storage` namespace
- [ ] `IStorageFile`, `IStorageDirectory`, `IStorageItem` in same namespace
- [ ] `GetFileAsync` returns `null` (not throws) when file doesn't exist
- [ ] `ReadAsync` accepts `offset` for range reads
- [ ] `ListAsync` returns `IAsyncEnumerable` (streaming)
- [ ] All methods accept `CancellationToken`
- [ ] Zero external package references in `Strg.Core`
- [ ] XML doc comments on all interface members

## Test Cases

- **TC-001**: `IStorageProvider` implementors can be substituted with `NSubstitute.Substitute.For<IStorageProvider>()` in tests
- **TC-002**: Verify interface members exactly match docs spec

## Implementation Tasks

- [ ] Create `src/Strg.Core/Storage/IStorageProvider.cs`
- [ ] Create `src/Strg.Core/Storage/IStorageItem.cs` (with both sub-interfaces)
- [ ] Add XML doc comments to all interface members
- [ ] Create test stub: `InMemoryStorageProvider` (for unit testing — see STRG-030)

## Security Review Checklist

- [ ] No path traversal prevention in interface (it's the caller's responsibility, documented)
- [ ] Interface doc comment notes: callers must sanitize paths before calling

## Definition of Done

- [ ] Interfaces created and compilable
- [ ] XML docs complete
- [ ] Zero new package dependencies in Strg.Core
