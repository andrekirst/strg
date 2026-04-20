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

Expose full CRUD operations for inbox rules via GraphQL mutations: create, update, delete, and duplicate. All mutations enforce tenant isolation (users can only manage their own rules and rules on drives they own). The `conditions` and `actions` inputs use structured types (not raw JSON strings) to provide type safety at the GraphQL layer.

## Technical Specification

### GraphQL input types (`src/Strg.GraphQL/Types/Input/Inbox/`)

```graphql
input CreateInboxRuleInput {
  name: String!
  description: String
  priority: Int = 0
  isEnabled: Boolean = true

  # Exactly one of userId or driveId must be set (validated in mutation handler)
  userId: ID           # omit for drive-scoped rule
  driveId: ID          # omit for user-scoped rule

  conditions: ConditionGroupInput!
  actions: [InboxActionInput!]!
}

input ConditionGroupInput {
  operator: LogicalOperator!     # AND (v0.1 only), OR, NOT
  children: [InboxConditionInput!]!
}

input InboxConditionInput {
  # Exactly one of the following is non-null (discriminated union pattern):
  group: ConditionGroupInput
  mimeType: MimeTypeConditionInput
  nameGlob: NameGlobConditionInput
  fileSize: FileSizeConditionInput
  uploadDateTime: UploadDateTimeConditionInput
}

input MimeTypeConditionInput { mimeType: String! }
input NameGlobConditionInput { pattern: String! }
input FileSizeConditionInput { minBytes: Long, maxBytes: Long }
input UploadDateTimeConditionInput {
  daysOfWeek: [DayOfWeek!]
  hourFrom: Int       # 0-23
  hourTo: Int         # 0-23
  after: DateTime
  before: DateTime
}

input InboxActionInput {
  # Exactly one of the following is non-null:
  move: MoveActionInput
  # v0.2: copy, rename, tag, webhook
}

input MoveActionInput {
  targetPath: String!
  targetDriveId: ID              # null = source file's drive
  conflictResolution: ConflictResolution = AUTO_RENAME
  autoCreateFolders: Boolean = true
}

enum LogicalOperator { AND OR NOT }
enum ConflictResolution { AUTO_RENAME OVERWRITE FAIL }
enum DayOfWeek { MONDAY TUESDAY WEDNESDAY THURSDAY FRIDAY SATURDAY SUNDAY }
```

### Payload types

```graphql
type CreateInboxRulePayload { rule: InboxRule! }
type UpdateInboxRulePayload { rule: InboxRule! }
type DeleteInboxRulePayload { deletedId: ID! }
type DuplicateInboxRulePayload { rule: InboxRule! }
```

### Mutations class (`src/Strg.GraphQL/Mutations/InboxRuleMutations.cs`)

```csharp
[MutationType]
public sealed class InboxRuleMutations
{
    public async Task<CreateInboxRulePayload> CreateInboxRuleAsync(
        CreateInboxRuleInput input,
        [Service] StrgDbContext db,
        [Service] ICurrentUserContext user,
        CancellationToken ct)
    {
        ValidateInput(input); // exactly one of userId/driveId set; at least one action; v0.1: only AND operator

        var rule = new InboxRule
        {
            TenantId = user.TenantId,
            UserId = input.UserId.HasValue ? Guid.Parse(input.UserId) : null,
            DriveId = input.DriveId.HasValue ? Guid.Parse(input.DriveId) : null,
            Name = input.Name,
            Description = input.Description,
            Priority = input.Priority,
            IsEnabled = input.IsEnabled,
            ConditionsJson = Serialize(input.Conditions),
            ActionsJson = Serialize(input.Actions)
        };

        db.InboxRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return new CreateInboxRulePayload(rule);
    }

    public async Task<DuplicateInboxRulePayload> DuplicateInboxRuleAsync(
        [ID] Guid id, [Service] StrgDbContext db, [Service] ICurrentUserContext user, CancellationToken ct)
    {
        var original = await db.InboxRules.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new NotFoundException(nameof(InboxRule), id);

        var copy = new InboxRule
        {
            TenantId = original.TenantId,
            UserId = original.UserId,
            DriveId = original.DriveId,
            Name = $"Copy of {original.Name}",
            Description = original.Description,
            Priority = original.Priority,
            IsEnabled = false,         // disabled by default
            ConditionsJson = original.ConditionsJson,
            ActionsJson = original.ActionsJson
        };

        db.InboxRules.Add(copy);
        await db.SaveChangesAsync(ct);
        return new DuplicateInboxRulePayload(copy);
    }
}
```

### Input validation rules

- Exactly one of `userId` / `driveId` must be set on create.
- v0.1 only supports `LogicalOperator.And` in condition groups — return validation error if `Or`/`Not` used.
- At least one action must be provided.
- For drive-scoped rules, the authenticated user must own the drive.
- `MoveActionInput.targetPath` must be a valid non-empty path (validated via `StoragePath.Parse` logic).

### `InboxRule` GraphQL output type

```graphql
type InboxRule {
  id: ID!
  name: String!
  description: String
  priority: Int!
  isEnabled: Boolean!
  scope: RuleScope!       # USER or DRIVE
  driveId: ID
  userId: ID
  conditions: ConditionGroup!
  actions: [InboxAction!]!
  createdAt: DateTime!
  updatedAt: DateTime!
}

enum RuleScope { USER DRIVE }
```

## Acceptance Criteria

- [ ] `createInboxRule` mutation persists rule with correct `ConditionsJson` and `ActionsJson`
- [ ] `updateInboxRule` mutation updates all mutable fields including JSON columns
- [ ] `deleteInboxRule` soft-deletes the rule; it no longer appears in queries
- [ ] `duplicateInboxRule` creates a copy with `IsEnabled = false` and name prefixed `"Copy of "`
- [ ] Attempting to manage a rule from a different tenant throws `NotFoundException`
- [ ] Using `OR`/`NOT` operators in v0.1 returns a validation error
- [ ] Drive-scoped rule creation rejected if user does not own the drive
- [ ] `InboxRule` GraphQL type exposes `conditions` and `actions` as structured objects (not raw JSON)

## Test Cases

- TC-001: `createInboxRule` with AND + MimeType + Move → rule persisted; conditions/actions round-trip
- TC-002: `createInboxRule` with OR operator → validation error returned
- TC-003: `updateInboxRule` changes priority and `isEnabled` → persisted correctly
- TC-004: `deleteInboxRule` soft-deletes → rule absent from `inboxRules` query
- TC-005: `duplicateInboxRule` → copy has `isEnabled = false`, name = "Copy of [original]"
- TC-006: Create rule on drive owned by another user → error
- TC-007: Both `userId` and `driveId` set on create → validation error

## Implementation Tasks

- [ ] Create GraphQL input types in `src/Strg.GraphQL/Types/Input/Inbox/`
- [ ] Create `src/Strg.GraphQL/Mutations/InboxRuleMutations.cs`
- [ ] Create `InboxRule` GraphQL output type with structured conditions/actions
- [ ] Implement input → domain model serialization helpers
- [ ] Add validation for v0.1 operator restrictions
- [ ] Write integration tests using GraphQL test client

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All TC-001 through TC-007 tests pass
- [ ] All mutations require authentication
- [ ] Input validation errors follow project's RFC 7807 error format
