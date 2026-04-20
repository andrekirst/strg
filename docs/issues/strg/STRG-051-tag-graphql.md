---
id: STRG-051
title: Create TagType and tag management GraphQL mutations
milestone: v0.1
priority: critical
status: done
type: implementation
labels: [graphql, tags]
depends_on: [STRG-049, STRG-046]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-051: Create TagType and tag management GraphQL mutations

## Summary

Create the GraphQL `Tag` type and mutations for adding, updating, and removing tags on files. Mutations live under the `storage` namespace and return Relay-style payload types. Tags are user-scoped and authenticated via JWT.

## Technical Specification

### Schema:

```graphql
type Tag implements Node {
  id: ID!
  key: String!
  value: String!
  valueType: TagValueType!
  createdAt: DateTime!
}

enum TagValueType { STRING INT FLOAT BOOL DATETIME }

# Under mutation { storage { ... } }
type StorageMutations {
  addTag(input: AddTagInput!): AddTagPayload!
  updateTag(input: UpdateTagInput!): UpdateTagPayload!
  removeTag(input: RemoveTagInput!): RemoveTagPayload!
  removeAllTags(input: RemoveAllTagsInput!): RemoveAllTagsPayload!
}

type AddTagPayload        { tag: Tag    errors: [UserError!] }
type UpdateTagPayload     { tag: Tag    errors: [UserError!] }
type RemoveTagPayload     { tagId: ID   errors: [UserError!] }
type RemoveAllTagsPayload { fileId: ID  errors: [UserError!] }

input AddTagInput        { fileId: ID!  key: String!  value: String!  valueType: TagValueType! }
input UpdateTagInput     { id: ID!  value: String!  valueType: TagValueType! }
input RemoveTagInput     { id: ID! }
input RemoveAllTagsInput { fileId: ID! }
```

### File: `src/Strg.GraphQL/Mutations/TagMutations.cs`

```csharp
[ExtendObjectType<StorageMutations>]
public sealed class TagMutations
{
    [Authorize(Policy = "TagsWrite")]
    public async Task<AddTagPayload> AddTagAsync(
        AddTagInput input,
        [Service] ITagService tagService,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        try
        {
            var tag = await tagService.UpsertAsync(input.FileId, userId, input.Key, input.Value, input.ValueType, ct);
            return new AddTagPayload(tag, null);
        }
        catch (NotFoundException ex)
        {
            return new AddTagPayload(null, [new UserError("NOT_FOUND", ex.Message, null)]);
        }
        catch (ValidationException ex)
        {
            return new AddTagPayload(null, [new UserError("VALIDATION_ERROR", ex.Message, ex.PropertyName)]);
        }
    }

    [Authorize(Policy = "TagsWrite")]
    public async Task<RemoveTagPayload> RemoveTagAsync(
        RemoveTagInput input,
        [Service] ITagService tagService,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        await tagService.RemoveAsync(input.Id, userId, ct);  // idempotent
        return new RemoveTagPayload(input.Id, null);
    }
}
```

### File: `src/Strg.GraphQL/Types/TagType.cs`

```csharp
public sealed class TagType : ObjectType<Tag>
{
    protected override void Configure(IObjectTypeDescriptor<Tag> descriptor)
    {
        descriptor.ImplementsNode().IdField(t => t.Id);
        descriptor.Field(t => t.TenantId).Ignore();
        descriptor.Field(t => t.UserId).Ignore();  // user isolation — not exposed
    }
}
```

## Acceptance Criteria

- [ ] `mutation { storage { addTag(input: { fileId: "...", key: "project", value: "acme", valueType: STRING }) { tag { id key value } errors { code } } } }` → tag created
- [ ] `addTag` on the same key → updates value (upsert behaviour from service)
- [ ] `removeTag` on non-existent id → `RemoveTagPayload` with no errors (idempotent)
- [ ] Tags require `tags.write` scope
- [ ] Adding a tag to another user's private file → `errors: [{ code: "NOT_FOUND" }]` (not FORBIDDEN — no file existence leak)
- [ ] `key` exceeding 255 chars → `errors: [{ code: "VALIDATION_ERROR", field: "key" }]`
- [ ] Tags appear in `FileItem.tags` for the authenticated user only
- [ ] `TagType` implements `Node` interface

## Test Cases

- **TC-001**: Add tag → `FileItem.tags` includes the new tag
- **TC-002**: Add same key twice → only one tag, value updated
- **TC-003**: Remove tag → `FileItem.tags` no longer includes it
- **TC-004**: Add tag with key > 255 chars → `errors[0].code = "VALIDATION_ERROR"`, `errors[0].field = "key"`
- **TC-005**: User A's tags not visible to User B on same file
- **TC-006**: `removeAllTags` returns correct `fileId` in payload

## Implementation Tasks

- [ ] Create `TagType.cs` in `src/Strg.GraphQL/Types/`
- [ ] Create `TagMutations.cs` in `src/Strg.GraphQL/Mutations/` with `[ExtendObjectType<StorageMutations>]`
- [ ] Create payload records `AddTagPayload`, `UpdateTagPayload`, `RemoveTagPayload`, `RemoveAllTagsPayload` in `src/Strg.GraphQL/Payloads/`
- [ ] Create input records `AddTagInput`, `UpdateTagInput`, `RemoveTagInput`, `RemoveAllTagsInput` in `src/Strg.GraphQL/Inputs/`
- [ ] Create `ITagService.cs` in `Strg.Core/Services/`
- [ ] Implement `TagService.cs` in `Strg.Infrastructure/`
- [ ] Types are auto-discovered by `AddTypes()` in STRG-049 — no manual registration

## Security Review Checklist

- [ ] `UserId` comes from JWT `[GlobalState("userId")]`, never from mutation input
- [ ] `TenantId` and `UserId` fields ignored in `TagType`
- [ ] Tags from user A cannot be queried by user B

## Definition of Done

- [ ] Upsert, remove, and removeAll mutations work with payload pattern
- [ ] User isolation verified
