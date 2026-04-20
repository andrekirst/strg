---
id: STRG-310
title: GraphQL InboxRule CRUD mutations
milestone: v0.1
priority: high
status: open
type: implementation
labels: [inbox, graphql]
depends_on: [STRG-302, STRG-049]
blocks: [STRG-311, STRG-328]
assigned_agent_type: feature-dev
estimated_complexity: medium
---

# STRG-310: GraphQL InboxRule CRUD mutations

## Summary

Expose full CRUD operations for inbox rules via GraphQL mutations under the `inbox` namespace. All mutations return Relay-style payload types with `errors: [UserError!]`. Conditions and actions are stored as JSON in v0.1 (structured SDL types planned for v0.2).

## Technical Specification

### Schema (under `mutation { inbox { ... } }`):

```graphql
type InboxMutations {
  createInboxRule(input: CreateInboxRuleInput!): CreateInboxRulePayload!
  updateInboxRule(input: UpdateInboxRuleInput!): UpdateInboxRulePayload!
  deleteInboxRule(input: DeleteInboxRuleInput!): DeleteInboxRulePayload!
  duplicateInboxRule(input: DuplicateInboxRuleInput!): DuplicateInboxRulePayload!
}

type CreateInboxRulePayload    { rule: InboxRule  errors: [UserError!] }
type UpdateInboxRulePayload    { rule: InboxRule  errors: [UserError!] }
type DeleteInboxRulePayload    { ruleId: ID       errors: [UserError!] }
type DuplicateInboxRulePayload { rule: InboxRule  errors: [UserError!] }

input CreateInboxRuleInput {
  name: String!
  priority: Int!
  conditionTree: JSON!   # { "$type": "and", "conditions": [...] }
  actions: JSON!         # [{ "$type": "move", "targetPath": "..." }]
  isEnabled: Boolean
}

input UpdateInboxRuleInput {
  id: ID!
  name: String
  priority: Int
  conditionTree: JSON
  actions: JSON
  isEnabled: Boolean
}

input DeleteInboxRuleInput    { id: ID! }
input DuplicateInboxRuleInput { id: ID!  newName: String }
```

### v0.1 JSON schema for conditionTree (documented, not enforced in SDL):

```json
{
  "$type": "and",
  "conditions": [
    { "$type": "mimeType", "mimeType": "image/*" },
    { "$type": "fileSize", "minBytes": 1048576 }
  ]
}
```

v0.1 supported condition `$type` values: `and`, `mimeType`, `nameGlob`, `fileSize`, `uploadDateTime`.
v0.1 supported operator: `and` only. Return `VALIDATION_ERROR` if `or`/`not` used.

### v0.1 JSON schema for actions:

```json
[
  {
    "$type": "move",
    "targetPath": "/sorted/images",
    "conflictResolution": "AUTO_RENAME",
    "autoCreateFolders": true
  }
]
```

### File: `src/Strg.GraphQL/Mutations/InboxRuleMutations.cs`

```csharp
[ExtendObjectType<InboxMutations>]
public sealed class InboxRuleMutations
{
    [Authorize]
    public async Task<CreateInboxRulePayload> CreateInboxRuleAsync(
        CreateInboxRuleInput input,
        [Service] StrgDbContext db,
        [GlobalState("userId")] Guid userId,
        [GlobalState("tenantId")] Guid tenantId,
        CancellationToken ct)
    {
        // Validate v0.1 restriction: only AND operator allowed
        if (!IsValidConditionTree(input.ConditionTree, out var error))
            return new CreateInboxRulePayload(null, [new UserError("VALIDATION_ERROR", error, "conditionTree")]);

        if (input.Actions is null || !input.Actions.Any())
            return new CreateInboxRulePayload(null, [new UserError("VALIDATION_ERROR",
                "At least one action is required.", "actions")]);

        var rule = new InboxRule
        {
            TenantId = tenantId,
            UserId = userId,
            Name = input.Name,
            Priority = input.Priority,
            IsEnabled = input.IsEnabled ?? true,
            ConditionsJson = input.ConditionTree.ToString(),
            ActionsJson = input.Actions.ToString()
        };

        db.InboxRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return new CreateInboxRulePayload(rule, null);
    }

    [Authorize]
    public async Task<DuplicateInboxRulePayload> DuplicateInboxRuleAsync(
        DuplicateInboxRuleInput input,
        [Service] StrgDbContext db,
        [GlobalState("userId")] Guid userId,
        [GlobalState("tenantId")] Guid tenantId,
        CancellationToken ct)
    {
        var original = await db.InboxRules.FirstOrDefaultAsync(
            r => r.Id == (Guid)input.Id && r.TenantId == tenantId, ct);

        if (original is null)
            return new DuplicateInboxRulePayload(null, [new UserError("NOT_FOUND", "Rule not found.", null)]);

        var copy = new InboxRule
        {
            TenantId = original.TenantId,
            UserId = original.UserId,
            Name = input.NewName ?? $"Copy of {original.Name}",
            Priority = original.Priority,
            IsEnabled = false,   // disabled by default
            ConditionsJson = original.ConditionsJson,
            ActionsJson = original.ActionsJson
        };

        db.InboxRules.Add(copy);
        await db.SaveChangesAsync(ct);
        return new DuplicateInboxRulePayload(copy, null);
    }
}
```

### `InboxRule` GraphQL output type:

```graphql
type InboxRule implements Node {
  id: ID!
  name: String!
  priority: Int!
  isEnabled: Boolean!
  conditionTree: JSON!
  actions: JSON!
  createdAt: DateTime!
  updatedAt: DateTime!
  executionLogs(first: Int, after: String): InboxRuleExecutionLogConnection!
}
```

## Acceptance Criteria

- [ ] `mutation { inbox { createInboxRule(input: { name: "...", priority: 10, conditionTree: {...}, actions: [...], isEnabled: true }) { rule { id name } errors { code field } } } }` → rule created
- [ ] `updateInboxRule` updates all mutable fields including JSON columns
- [ ] `deleteInboxRule` soft-deletes the rule; it no longer appears in queries
- [ ] `duplicateInboxRule` creates a copy with `isEnabled = false`, name prefixed `"Copy of "` if `newName` not provided
- [ ] Rule from different tenant → `errors: [{ code: "NOT_FOUND" }]`
- [ ] Using `or`/`not` operators in `conditionTree` → `errors: [{ code: "VALIDATION_ERROR", field: "conditionTree" }]`
- [ ] Empty `actions` array → `errors: [{ code: "VALIDATION_ERROR", field: "actions" }]`
- [ ] `InboxRule` type implements `Node` interface

## Test Cases

- TC-001: `createInboxRule` with AND + mimeType condition + move action → rule persisted; JSON round-trips correctly
- TC-002: `createInboxRule` with `or` operator → `errors[0].code = "VALIDATION_ERROR"`
- TC-003: `updateInboxRule` changes `priority` and `isEnabled` → persisted correctly
- TC-004: `deleteInboxRule` soft-deletes → rule absent from `inbox { rules }` query
- TC-005: `duplicateInboxRule` → copy has `isEnabled = false`; `name = "Copy of [original]"`
- TC-006: Manage rule from different tenant → `errors[0].code = "NOT_FOUND"`
- TC-007: `createInboxRule` with empty `actions` → `errors[0].field = "actions"`

## Implementation Tasks

- [ ] Create `InboxMutations` marker record in `src/Strg.GraphQL/Mutations/`
- [ ] Create `InboxRuleMutations.cs` with `[ExtendObjectType<InboxMutations>]`
- [ ] Create payload records in `src/Strg.GraphQL/Payloads/`
- [ ] Create input records in `src/Strg.GraphQL/Inputs/`
- [ ] Add condition tree validator for v0.1 (AND only)
- [ ] Types auto-discovered by `AddTypes()` — no manual registration

## Security Review Checklist

- [ ] `UserId` and `TenantId` from JWT, never from mutation input
- [ ] `TenantId` filter applied when loading rules for update/delete/duplicate
- [ ] `StoragePath.Parse()` called on `targetPath` in move actions before persisting

## Definition of Done

- [ ] All four mutations working with payload pattern in integration tests
- [ ] JSON condition/action round-trip verified
