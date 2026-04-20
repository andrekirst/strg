---
id: STRG-311
title: GraphQL inboxRules + inboxFiles queries
milestone: v0.1
priority: high
status: open
type: implementation
labels: [inbox, graphql]
depends_on: [STRG-302, STRG-304, STRG-305, STRG-049]
blocks: [STRG-326]
assigned_agent_type: feature-dev
estimated_complexity: medium
---

# STRG-311: GraphQL inboxRules + inboxFiles queries

## Summary

Add read-side GraphQL queries for the inbox feature: listing rules, querying files currently in the inbox by status, filtering the existing `files` query by inbox state, and querying the permanent execution log. All queries use DataLoader for the `rule` and `file` relations to prevent N+1 problems.

## Technical Specification

### New queries (`src/Strg.GraphQL/Queries/InboxQueries.cs`)

```graphql
type Query {
  # List all rules for a given scope, ordered by priority
  inboxRules(scope: RuleScope!, driveId: ID): [InboxRule!]!

  # Fetch a single rule by ID
  inboxRule(id: ID!): InboxRule

  # Files currently in the inbox, optionally filtered by processing status
  inboxFiles(status: InboxFileStatus): [FileItem!]!

  # Full execution log — paginated, filterable by file or rule
  inboxRuleExecutionLogs(
    fileId: ID
    ruleId: ID
    first: Int = 20
    after: String
  ): InboxRuleExecutionLogConnection!
}
```

### inboxRules implementation

```csharp
public async Task<IReadOnlyList<InboxRule>> GetInboxRulesAsync(
    RuleScope scope, Guid? driveId,
    [Service] StrgDbContext db,
    [Service] ICurrentUserContext user,
    CancellationToken ct)
{
    return scope switch
    {
        RuleScope.User => await db.InboxRules
            .Where(r => r.UserId == user.UserId)
            .OrderBy(r => r.Priority)
            .ToListAsync(ct),
        RuleScope.Drive when driveId.HasValue => await db.InboxRules
            .Where(r => r.DriveId == driveId)
            .OrderBy(r => r.Priority)
            .ToListAsync(ct),
        _ => throw new ValidationException("driveId is required when scope is DRIVE")
    };
}
```

### inboxFiles implementation

```csharp
public async Task<IReadOnlyList<FileItem>> GetInboxFilesAsync(
    InboxFileStatus? status,
    [Service] StrgDbContext db,
    CancellationToken ct)
{
    var query = db.Files.Where(f => f.IsInInbox);
    if (status.HasValue)
        query = query.Where(f => f.InboxStatus == status);
    return await query.OrderByDescending(f => f.InboxEnteredAt).ToListAsync(ct);
}
```

### Extend existing files query

In the existing `FileItemQueries`, add an `inInbox: Boolean` filter parameter:

```csharp
// In existing files query handler
if (filter?.InInbox == true)
    query = query.Where(f => f.IsInInbox);
else if (filter?.InInbox == false)
    query = query.Where(f => !f.IsInInbox);
```

### Execution log query with cursor pagination

```csharp
public async Task<Connection<InboxRuleExecutionLog>> GetInboxRuleExecutionLogsAsync(
    Guid? fileId, Guid? ruleId,
    [Service] StrgDbContext db,
    CancellationToken ct)
{
    var query = db.InboxRuleExecutionLogs.AsQueryable();
    if (fileId.HasValue) query = query.Where(l => l.FileId == fileId);
    if (ruleId.HasValue) query = query.Where(l => l.RuleId == ruleId);
    return await query.OrderByDescending(l => l.EvaluatedAt).ApplyCursorPaginationAsync(ct);
}
```

### GraphQL type extensions

```graphql
# Extend FileItem type with inbox fields
extend type FileItem {
  isInInbox: Boolean!
  inboxStatus: InboxFileStatus
  inboxEnteredAt: DateTime
  inboxExitedAt: DateTime
}

type InboxRuleExecutionLog {
  id: ID!
  fileId: ID!
  file: FileItem!           # DataLoader for file
  ruleId: ID
  rule: InboxRule           # DataLoader for rule; null if Skipped
  evaluatedAt: DateTime!
  matched: Boolean!
  actionsTaken: [InboxActionSummary!]
  status: InboxRuleLogStatus!
  notes: String
}

type InboxRuleExecutionLogConnection {
  nodes: [InboxRuleExecutionLog!]!
  pageInfo: PageInfo!
  totalCount: Int!
}

type InboxActionSummary {
  actionType: String!
  targetPath: String
  success: Boolean!
}

enum InboxFileStatus { PENDING PROCESSING PROCESSED PARTIAL_FAILURE FAILED SKIPPED }
enum InboxRuleLogStatus { MATCHED NO_MATCH FAILED SKIPPED }
```

### DataLoaders

- `InboxRuleByIdDataLoader` — batch-load rules by ID for log entries
- `FileItemByIdDataLoader` — reuse existing if available, or create for log entries

## Acceptance Criteria

- [ ] `inboxRules(scope: USER)` returns authenticated user's rules ordered by priority
- [ ] `inboxRules(scope: DRIVE, driveId: "...")` returns drive's rules ordered by priority
- [ ] `inboxRule(id: "...")` returns single rule or null
- [ ] `inboxFiles` returns only files where `IsInInbox = true`
- [ ] `inboxFiles(status: PENDING)` correctly filters by `InboxStatus`
- [ ] `files(inInbox: true)` filter works on the existing files query
- [ ] `inboxRuleExecutionLogs` supports pagination and filtering by `fileId` or `ruleId`
- [ ] Log entries resolve `rule` and `file` via DataLoader (no N+1)
- [ ] All queries respect tenant isolation (no cross-tenant data)
- [ ] All queries require authentication

## Test Cases

- TC-001: `inboxRules(scope: USER)` — returns only calling user's rules; another user's rules not included
- TC-002: `inboxFiles(status: PENDING)` — returns only files with `InboxStatus = Pending`
- TC-003: `files(inInbox: true)` — includes only inbox files; `files(inInbox: false)` excludes them
- TC-004: `inboxRuleExecutionLogs(fileId: "x")` — returns all log entries for that file
- TC-005: Log entry `rule` field resolved via DataLoader without extra queries
- TC-006: Paginating `inboxRuleExecutionLogs` with `first: 5, after: "cursor"` works correctly
- TC-007: `inboxRules(scope: DRIVE)` without `driveId` returns validation error

## Implementation Tasks

- [ ] Create `src/Strg.GraphQL/Queries/InboxQueries.cs`
- [ ] Create `InboxRuleExecutionLog` GraphQL type and connection type
- [ ] Extend `FileItem` GraphQL type with inbox fields
- [ ] Add `inInbox` filter to existing `files` query
- [ ] Create `InboxRuleByIdDataLoader`
- [ ] Write integration tests using GraphQL test client

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All TC-001 through TC-007 tests pass
- [ ] No N+1 queries detectable in integration tests (use query count assertion)
