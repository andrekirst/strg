---
id: STRG-030
title: Implement InMemoryStorageProvider for testing
milestone: v0.1
priority: high
status: done
type: implementation
labels: [testing, storage]
depends_on: [STRG-021]
blocks: []
assigned_agent_type: general-purpose
estimated_complexity: small
---

# STRG-030: Implement InMemoryStorageProvider for testing

## Summary

Create an `InMemoryStorageProvider` that implements `IStorageProvider` entirely in memory. Used in unit tests for `FileService`, `QuotaService`, and any code that depends on `IStorageProvider` without touching the filesystem.

## Technical Specification

```csharp
public class InMemoryStorageProvider : IStorageProvider
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _dirs = new();

    public string ProviderType => "memory";

    public Task WriteAsync(string path, Stream content, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        content.CopyToAsync(ms, ct).GetAwaiter().GetResult();
        _files[StoragePath.Parse(path).Value] = ms.ToArray();
        return Task.CompletedTask;
    }
    // ... all other methods
}
```

This provider is in `Strg.Infrastructure` (or a test helper project). It is NOT registered in production — only in test DI configurations.

## Acceptance Criteria

- [ ] Implements all `IStorageProvider` methods
- [ ] Thread-safe concurrent read/write
- [ ] `GetFileAsync` returns `null` when not found
- [ ] `ListAsync` returns only files in the specified path prefix
- [ ] Can simulate a file with specific content size for quota testing
- [ ] `ClearAll()` helper method for test teardown

## Test Cases

- **TC-001**: Write + Read → content matches
- **TC-002**: Write + `GetFileAsync` → `IStorageFile.Size` matches written bytes
- **TC-003**: Two concurrent writes to different paths → no deadlock
- **TC-004**: `ListAsync("docs/")` → returns only files under `docs/`

## Implementation Tasks

- [ ] Create `InMemoryStorageProvider.cs`
- [ ] Implement all interface methods
- [ ] Add to test DI helper

## Definition of Done

- [ ] Used in at least 5 unit tests across the codebase
- [ ] All interface methods implemented and tested
