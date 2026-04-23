---
id: STRG-047
title: Implement TagService in Strg.Infrastructure
milestone: v0.1
priority: high
status: done
type: implementation
labels: [infrastructure, tags]
depends_on: [STRG-046, STRG-004]
blocks: [STRG-051]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-047: Implement TagService in Strg.Infrastructure

## Summary

Implement `TagService` that backs the `ITagService` interface for upsert, remove, and query operations on `Tag` entities. Tags are user-scoped: each user has their own set of tags per file.

## Technical Specification

### Interface: `src/Strg.Core/Services/ITagService.cs`

```csharp
public interface ITagService
{
    Task<Tag> UpsertAsync(Guid fileId, Guid userId, string key, string value, CancellationToken ct);
    Task<bool> RemoveAsync(Guid fileId, Guid userId, string key, CancellationToken ct);
    Task<int> RemoveAllAsync(Guid fileId, Guid userId, CancellationToken ct);
    Task<IReadOnlyList<Tag>> GetTagsAsync(Guid fileId, Guid userId, CancellationToken ct);
}
```

### File: `src/Strg.Infrastructure/Services/TagService.cs`

```csharp
public sealed class TagService : ITagService
{
    private readonly ITagRepository _repo;

    public TagService(ITagRepository repo) => _repo = repo;

    public async Task<Tag> UpsertAsync(
        Guid fileId, Guid userId, string key, string value, CancellationToken ct)
    {
        ValidateKey(key);
        ValidateValue(value);

        var existing = await _repo.GetByKeyAsync(fileId, userId, key, ct);
        if (existing is not null)
        {
            existing.Value = value;
            await _repo.SaveChangesAsync(ct);
            return existing;
        }

        var tag = new Tag
        {
            FileId = fileId,
            UserId = userId,
            Key = key,
            Value = value,
            TenantId = /* from context */
        };
        _repo.Add(tag);
        await _repo.SaveChangesAsync(ct);
        return tag;
    }

    public async Task<bool> RemoveAsync(
        Guid fileId, Guid userId, string key, CancellationToken ct)
    {
        var tag = await _repo.GetByKeyAsync(fileId, userId, key, ct);
        if (tag is null) return false; // idempotent

        _repo.Remove(tag);
        await _repo.SaveChangesAsync(ct);
        return true;
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 255)
            throw new ValidationException("Tag key must be 1–255 characters.");
    }

    private static void ValidateValue(string value)
    {
        if (value.Length > 255)
            throw new ValidationException("Tag value must not exceed 255 characters.");
    }
}
```

### Key comparison:

Tag keys are case-insensitive. The DB unique constraint is on `LOWER(key)` (see STRG-046). The service normalizes keys to lowercase before all operations.

## Acceptance Criteria

- [ ] `UpsertAsync` with new key → creates tag
- [ ] `UpsertAsync` with existing key → updates value (no duplicate created)
- [ ] `RemoveAsync` on non-existent key → returns `false` (no exception)
- [ ] Key > 255 chars → `ValidationException`
- [ ] Value > 255 chars → `ValidationException`
- [ ] Tag key comparison is case-insensitive (`"Project"` == `"project"`)

## Test Cases

- **TC-001**: Upsert → tag created with correct key/value
- **TC-002**: Upsert same key twice → only one tag, value updated
- **TC-003**: Remove non-existent key → returns false, no exception
- **TC-004**: Key 256 chars → `ValidationException`
- **TC-005**: Upsert `"Project"` then query `"project"` → same tag returned

## Implementation Tasks

- [ ] Create `ITagService.cs` in `Strg.Core/Services/`
- [ ] Create `TagService.cs` in `Strg.Infrastructure/Services/`
- [ ] Implement `ITagRepository` in `Strg.Infrastructure/Repositories/TagRepository.cs`
- [ ] Register `ITagService` and `ITagRepository` in DI

## Testing Tasks

- [ ] Unit test: upsert creates/updates correctly
- [ ] Unit test: key validation
- [ ] Integration test: case-insensitive key matching

## Security Review Checklist

- [ ] `UserId` comes from service parameter, not from client (enforced by mutation layer)
- [ ] Tag key cannot be used for SQL injection (parameterized EF Core queries)

## Code Review Checklist

- [ ] Key normalized to lowercase before DB operations
- [ ] `TagService` is `sealed`
- [ ] Validation methods are `static`

## Definition of Done

- [ ] Upsert, remove, and query work
- [ ] Case-insensitive key matching verified
