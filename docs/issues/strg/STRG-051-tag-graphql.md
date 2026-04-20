---
id: STRG-051
title: Create TagType and tag management GraphQL mutations
milestone: v0.1
priority: critical
status: open
type: implementation
labels: [graphql, tags]
depends_on: [STRG-049, STRG-046]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-051: Create TagType and tag management GraphQL mutations

## Summary

Create the GraphQL `Tag` type and mutations for adding, updating, and removing tags on files. Tags are user-scoped and authenticated via JWT.

## Technical Specification

### Schema:

```graphql
type Tag {
  id: UUID!
  key: String!
  value: String!
  createdAt: DateTime!
}

type Mutation {
  addTag(fileId: UUID!, key: String!, value: String!): Tag!
  updateTag(fileId: UUID!, key: String!, value: String!): Tag!
  removeTag(fileId: UUID!, key: String!): Boolean!
  removeAllTags(fileId: UUID!): Int!  # returns count removed
}
```

### File: `src/Strg.GraphQL/Mutations/TagMutations.cs`

```csharp
[ExtendObjectType("Mutation")]
public class TagMutations
{
    [Authorize(Policy = "TagsWrite")]
    [Error(typeof(FileNotFoundException))]
    [Error(typeof(AccessDeniedException))]
    public async Task<Tag> AddTag(
        Guid fileId, string key, string value,
        [Service] ITagService tagService,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        return await tagService.UpsertAsync(fileId, userId, key, value, ct);
    }
}
```

## Acceptance Criteria

- [ ] `mutation { addTag(fileId: "...", key: "project", value: "acme") { id key value } }` → tag created
- [ ] `addTag` on the same key → updates value (upsert)
- [ ] `removeTag` on non-existent key → `false` returned (idempotent, not error)
- [ ] Tags require `tags.write` scope
- [ ] Adding a tag to another user's private file → `AccessDeniedException` (GraphQL error)
- [ ] `key` exceeding 255 chars → validation error
- [ ] `value` exceeding 255 chars → validation error
- [ ] Tags appear in `FileItem.tags` for the authenticated user only

## Test Cases

- **TC-001**: Add tag → `FileItem.tags` includes the new tag
- **TC-002**: Add same key twice → only one tag, value updated
- **TC-003**: Remove tag → `FileItem.tags` no longer includes it
- **TC-004**: Add tag with key > 255 chars → GraphQL validation error
- **TC-005**: User A's tags not visible to User B on same file

## Implementation Tasks

- [ ] Create `TagType.cs`
- [ ] Create `TagMutations.cs` with `AddTag`, `RemoveTag`, `RemoveAllTags`
- [ ] Create `ITagService.cs` in `Strg.Core.Services`
- [ ] Implement `TagService.cs` in `Strg.Infrastructure`
- [ ] Add `[UseFiltering]` on `FileItem.tags` for tag-based queries
- [ ] Register type in Hot Chocolate setup

## Security Review Checklist

- [ ] `UserId` comes from JWT, not from mutation argument (never trust client-supplied userId)
- [ ] Tags from user A cannot be queried by user B

## Definition of Done

- [ ] Upsert and remove mutations work
- [ ] User isolation verified
