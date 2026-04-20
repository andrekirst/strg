---
id: STRG-023
title: Create StorageProviderRegistry — resolve providers by drive type
milestone: v0.1
priority: critical
status: open
type: implementation
labels: [infrastructure, storage]
depends_on: [STRG-021]
blocks: [STRG-024, STRG-026]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-023: Create StorageProviderRegistry — resolve providers by drive type

## Summary

Create a registry that maps `providerType` strings (e.g., `"local"`, `"s3"`, `"ipfs"`) to `IStorageProvider` factory functions. Plugins register new providers here. Given a drive's `ProviderType`, the registry resolves the correct provider instance.

## Technical Specification

### File: `src/Strg.Core/Storage/IStorageProviderRegistry.cs`

```csharp
public interface IStorageProviderRegistry
{
    void Register(string providerType, Func<IStorageProviderConfig, IStorageProvider> factory);
    IStorageProvider Resolve(string providerType, IStorageProviderConfig config);
    bool IsRegistered(string providerType);
    IReadOnlyList<string> GetRegisteredTypes();
}
```

### File: `src/Strg.Infrastructure/Storage/StorageProviderRegistry.cs`

Implementation backed by a `ConcurrentDictionary<string, Func<...>>`.

### Registration pattern:

```csharp
services.AddSingleton<IStorageProviderRegistry, StorageProviderRegistry>();
services.AddSingleton<IStorageProvider, LocalFileSystemProvider>();

// In startup:
var registry = app.Services.GetRequiredService<IStorageProviderRegistry>();
registry.Register("local", config => new LocalFileSystemProvider(config));
```

### `IStorageProviderConfig`:

```csharp
public interface IStorageProviderConfig
{
    string GetValue(string key);
    T GetValue<T>(string key);
}
```

Backed by a `JsonElement` from the drive's `provider_config` JSON column.

## Acceptance Criteria

- [ ] `Register("local", factory)` → `IsRegistered("local") == true`
- [ ] `Resolve("local", config)` → calls the registered factory
- [ ] `Resolve("unknown", config)` → throws `InvalidOperationException` with clear message
- [ ] `GetRegisteredTypes()` returns all registered types
- [ ] Plugins can call `Register()` during startup to add new types
- [ ] Thread-safe (concurrent reads and writes)

## Test Cases

- **TC-001**: Register + Resolve → returns provider instance
- **TC-002**: Resolve unregistered type → `InvalidOperationException`
- **TC-003**: Register same type twice → second registration overwrites (or throws, document which)
- **TC-004**: Two threads registering simultaneously → no race condition

## Implementation Tasks

- [ ] Create `IStorageProviderRegistry.cs` in `Strg.Core`
- [ ] Create `StorageProviderRegistry.cs` in `Strg.Infrastructure`
- [ ] Create `IStorageProviderConfig.cs` and `JsonStorageProviderConfig.cs`
- [ ] Register "local" provider in startup
- [ ] Write unit tests

## Security Review Checklist

- [ ] `Resolve` with an unknown type fails fast (no fallback to dangerous defaults)
- [ ] Provider config values are not logged (may contain credentials like S3 secrets)

## Definition of Done

- [ ] Registry works with LocalFileSystemProvider
- [ ] Plugin registration pattern documented in `docs/requirements/06-plugin-system.md`
