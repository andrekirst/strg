---
id: STRG-046
title: Create Tag domain entity and ITagRepository
milestone: v0.1
priority: critical
status: open
type: implementation
labels: [core, domain, tags]
depends_on: [STRG-003, STRG-031]
blocks: [STRG-047, STRG-052]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-046: Create Tag domain entity and ITagRepository

## Summary

Create the `Tag` domain entity (key-value pair scoped to a user and file) and `ITagRepository`. Tags are user-scoped: the same key on the same file for two different users are independent records.

## Technical Specification

### File: `src/Strg.Core/Domain/Tag.cs`

```csharp
public sealed class Tag : TenantedEntity
{
    public Guid FileId { get; init; }
    public Guid UserId { get; init; }
    public required string Key { get; init; }
    public required string Value { get; set; }
    public TagValueType ValueType { get; set; } = TagValueType.String;
}

public enum TagValueType { String, Number, Boolean }
```

### Constraints:
- `Key`: max 255 chars, alphanumeric + hyphens + dots, case-insensitive for uniqueness
- `Value`: max 255 chars, stored as string regardless of `ValueType` (client interprets via type)
- `ValueType`: stored as `VARCHAR(10)` with DB check constraint (`'string'`, `'number'`, `'boolean'`)
- Unique constraint: `(FileId, UserId, LOWER(Key))` — one value per key per user per file

### File: `src/Strg.Core/Domain/ITagRepository.cs`

```csharp
public interface ITagRepository
{
    Task<IReadOnlyList<Tag>> GetByFileAsync(Guid fileId, Guid userId, CancellationToken ct = default);
    Task<Tag?> GetByKeyAsync(Guid fileId, Guid userId, string key, CancellationToken ct = default);
    Task UpsertAsync(Tag tag, CancellationToken ct = default);   // add or update
    Task RemoveAsync(Guid fileId, Guid userId, string key, CancellationToken ct = default);
    Task RemoveAllAsync(Guid fileId, Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<Tag>> SearchAsync(Guid tenantId, Guid userId, string? key, string? value, CancellationToken ct = default);
}
```

## Acceptance Criteria

- [ ] `Tag` inherits `TenantedEntity`
- [ ] `Tag.Key` is immutable (`init`)
- [ ] `Tag.Value` is mutable (can be updated via `UpsertAsync`)
- [ ] `ITagRepository.UpsertAsync` adds tag if not exists, updates if exists (based on unique key)
- [ ] `Key` comparison is case-insensitive (index uses `LOWER(key)`)
- [ ] `ITagRepository.SearchAsync` supports filtering by key, value, or both
- [ ] `ITagRepository` in `Strg.Core.Domain`

## Test Cases

- **TC-001**: Add tag `{ key: "project", value: "acme" }` → `GetByKey` returns it
- **TC-002**: Add same key twice with different values → second upsert updates, not duplicates
- **TC-003**: `Tag.Key = "Project"` and `Tag.Key = "project"` are the same key (case-insensitive)
- **TC-004**: Tag from user A and user B on same file with same key → two independent records
- **TC-005**: `RemoveAsync` on non-existent key → silently succeeds (idempotent)

## Implementation Tasks

- [ ] Create `Tag.cs`
- [ ] Create `ITagRepository.cs`
- [ ] Implement `TagRepository.cs` in `Strg.Infrastructure`
- [ ] Add EF Core configuration with unique index on `(file_id, user_id, LOWER(key))`
- [ ] Write unit tests for repository

## Security Review Checklist

- [ ] Tag key and value are sanitized for control characters
- [ ] A user can only query/modify their own tags (UserId from JWT, not from request body)
- [ ] Tag search cannot retrieve other users' tags (always filtered by UserId)

## Definition of Done

- [ ] Entity and repository created
- [ ] Unique constraint enforced
- [ ] User isolation verified in tests
