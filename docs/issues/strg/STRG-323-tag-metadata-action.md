---
id: STRG-323
title: Tag/metadata action
milestone: v0.2
priority: low
status: open
type: implementation
labels: [inbox, actions, tags, metadata]
depends_on: [STRG-308, STRG-046]
blocks: []
assigned_agent_type: feature-dev
estimated_complexity: small
---

# STRG-323: Tag/metadata action

## Summary

Implement the `TagAction` inbox action type, which automatically applies tags and custom metadata fields to a file as part of rule processing. Tags are applied using the existing tag system (STRG-046/047). The action runs after any Move action in the same rule, so tags land on the file at its final location.

## Technical Specification

### New action type (`src/Strg.Core/Domain/Inbox/InboxAction.cs`)

```csharp
[JsonDerivedType(typeof(TagAction), "tag")]
```

```csharp
public record TagAction(
    IReadOnlyList<string> TagsToAdd,
    IReadOnlyList<MetadataEntry> MetadataToSet
) : InboxAction(ConflictResolution.AutoRename, AutoCreateFolders: false);

public record MetadataEntry(string Key, string Value, string ValueType = "string");
```

### Action execution (in `InboxProcessingConsumer`)

Add case to `ExecuteActionAsync`:

```csharp
if (action is TagAction tag)
{
    foreach (var tagName in tag.TagsToAdd)
        await _tagService.AddTagToFileAsync(file.Id, tagName, ct);

    foreach (var meta in tag.MetadataToSet)
        await _metadataService.SetAsync(file.Id, meta.Key, meta.Value, meta.ValueType, ct);
}
```

The tag service used here is `ITagService` from STRG-046. Tags are created if they don't exist within the tenant.

### Action ordering guarantee

`InboxProcessingConsumer` executes actions in the order they appear in `ActionsJson`. To ensure tagging happens after moving, the rule creator should place `TagAction` after `MoveAction` in the actions array. This is documented in the GraphQL schema description, not enforced by code.

### GraphQL input type update

Add `TagActionInput` to `InboxActionInput`:

```graphql
input TagActionInput {
  tagsToAdd: [String!]!
  metadataToSet: [MetadataEntryInput!]
}

input MetadataEntryInput {
  key: String!
  value: String!
  valueType: String = "string"
}
```

## Acceptance Criteria

- [ ] `TagAction` record exists in `Strg.Core` with `[JsonDerivedType]`
- [ ] `MetadataEntry` record exists in `Strg.Core`
- [ ] `InboxProcessingConsumer` handles `TagAction` in `ExecuteActionAsync`
- [ ] Tags are created in the tenant if they don't already exist
- [ ] Idempotency: applying the same tag twice does not create duplicates (handled by tag service)
- [ ] `TagActionInput` exposed in the GraphQL mutations

## Test Cases

- TC-001: Rule with `TagAction(["photo", "auto-sorted"], [])` → file has both tags after processing
- TC-002: Tag that doesn't exist yet → created and applied
- TC-003: Tag already applied to file → idempotent (no duplicate, no error)
- TC-004: `TagAction` after `MoveAction` in same rule → both execute; file has correct path AND tags
- TC-005: `MetadataEntry("source", "inbox", "string")` → metadata key set on file

## Implementation Tasks

- [ ] Add `TagAction` and `MetadataEntry` to `InboxAction.cs`
- [ ] Add `ExecuteActionAsync` case for `TagAction` in `InboxProcessingConsumer`
- [ ] Add `TagActionInput` GraphQL input type
- [ ] Write integration tests

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All TC-001 through TC-005 tests pass
