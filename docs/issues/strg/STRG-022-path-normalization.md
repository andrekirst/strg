---
id: STRG-022
title: Implement StoragePath — path normalization and traversal protection
milestone: v0.1
priority: critical
status: open
type: implementation
labels: [core, storage, security]
depends_on: [STRG-021]
blocks: [STRG-024, STRG-025]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-022: Implement StoragePath — path normalization and traversal protection

## Summary

Create a `StoragePath` value type that validates and normalizes storage paths, blocking all path traversal attempts. All `IStorageProvider` implementations MUST use this for input paths.

## Technical Specification

### File: `src/Strg.Core/Storage/StoragePath.cs`

```csharp
namespace Strg.Core.Storage;

public readonly struct StoragePath
{
    public string Value { get; }

    private StoragePath(string value) => Value = value;

    public static StoragePath Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (ContainsTraversal(raw)) throw new StoragePathException($"Path traversal detected: {raw}");
        if (ContainsNullByte(raw)) throw new StoragePathException("Null byte in path");
        if (IsReservedName(raw)) throw new StoragePathException($"Reserved path: {raw}");
        return new StoragePath(Normalize(raw));
    }

    public static bool TryParse(string raw, out StoragePath path) { ... }

    private static bool ContainsTraversal(string p) =>
        p.Contains("..") || p.Contains("//") || p.StartsWith('/');

    private static bool ContainsNullByte(string p) => p.Contains('\0');

    private static string Normalize(string p) =>
        p.Replace('\\', '/').TrimStart('/').TrimEnd('/');

    // Windows reserved: CON, PRN, AUX, NUL, COM1-9, LPT1-9
    private static bool IsReservedName(string p) { ... }
}

public sealed class StoragePathException(string message) : Exception(message);
```

## Acceptance Criteria

- [ ] `StoragePath.Parse("../../etc/passwd")` throws `StoragePathException`
- [ ] `StoragePath.Parse("../secret")` throws
- [ ] `StoragePath.Parse("docs/report.pdf")` succeeds
- [ ] `StoragePath.Parse("/absolute/path")` throws (absolute paths not allowed)
- [ ] `StoragePath.Parse("docs//double-slash")` throws
- [ ] `StoragePath.Parse("CON")` throws (Windows reserved)
- [ ] Backslashes normalized to forward slashes
- [ ] Trailing slashes stripped
- [ ] Path with null byte (`"file\0.txt"`) throws
- [ ] `StoragePath` is a `readonly struct` (value type, zero allocation)

## Test Cases

- **TC-001**: `Parse("../../etc/passwd")` → `StoragePathException`
- **TC-002**: `Parse("docs/report.pdf")` → `Value == "docs/report.pdf"`
- **TC-003**: `Parse("docs\\report.pdf")` → `Value == "docs/report.pdf"` (backslash normalized)
- **TC-004**: `Parse("/absolute")` → `StoragePathException`
- **TC-005**: `Parse("docs//double")` → `StoragePathException`
- **TC-006**: `Parse("docs/report.pdf/")` → `Value == "docs/report.pdf"` (trailing slash stripped)
- **TC-007**: `Parse("CON")` → `StoragePathException`
- **TC-008**: 100,000 calls to `Parse` with valid path → no allocations (benchmark)

## Implementation Tasks

- [ ] Create `src/Strg.Core/Storage/StoragePath.cs`
- [ ] Create `src/Strg.Core/Storage/StoragePathException.cs`
- [ ] Implement all validation cases
- [ ] Write comprehensive unit tests (all TC above)
- [ ] Document usage requirement in `IStorageProvider` XML docs

## Security Review Checklist

- [ ] Traversal check covers: `..`, `//`, absolute paths, URL-encoded variants (`%2E%2E`)
- [ ] URL-decoding happens BEFORE traversal check (to catch encoded traversal)
- [ ] `StoragePath` is used everywhere storage paths are accepted (no raw string paths)
- [ ] Windows reserved names list is complete (CON, PRN, AUX, NUL, COM1-9, LPT1-9)

## Definition of Done

- [ ] All test cases pass
- [ ] Security review completed
- [ ] Zero Strg.Core package dependencies added
