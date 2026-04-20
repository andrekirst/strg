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

Add read-side GraphQL queries for the inbox feature under the `InboxQueries` namespace. All list queries use Relay cursor pagination. DataLoaders are used for related entity resolution to prevent N+1 queries.

## Technical Specification

### Schema (under `query { inbox { ... } }`):

```graphql
type InboxQueries {
  rules(first: Int, after: String, last: Int, before: String): InboxRuleConnection!
  rule(id: ID!): InboxRule
  files(first: Int, after: String, last: Int, before: String, filter: FileFilterInput): FileItemConnection!
  executionLogs(first: Int, after: String, last: Int, before: String, fileId: ID, ruleId: ID): InboxRuleExecutionLogConnection!
}

type InboxRuleExecutionLog implements Node {
  id: ID!
  file: FileItem!       # resolved via FileItemByIdDataLoader
  rule: InboxRule!      # resolved via InboxRuleByIdDataLoader
  matched: Boolean!
  actionsTaken: JSON!
  evaluatedAt: DateTime!
}
```

### File: `src/Strg.GraphQL/Queries/InboxQueries.cs`

```csharp
public sealed record InboxQueries;  // namespace marker

[ExtendObjectType<InboxQueries>]
public sealed class InboxRuleQueries
{
    [UsePaging(DefaultPageSize = 50, MaxPageSize = 200)]
    [GraphQLComplexity(5)]
    [Authorize]
    public IQueryable<InboxRule> GetRules(
        [Service] StrgDbContext db,
        [GlobalState("userId")] Guid userId)
        => db.InboxRules
             .Where(r => r.UserId == userId)
             .OrderBy(r => r.Priority);

    [Authorize]
    public Task<InboxRule?> GetRule(
        ID id,
        [Service] StrgDbContext db,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
        => db.InboxRules.FirstOrDefaultAsync(
               r => r.Id == (Guid)id && r.UserId == userId, ct);

    [UsePaging(DefaultPageSize = 50, MaxPageSize = 200)]
    [GraphQLComplexity(5)]
    [Authorize]
    public IQueryable<FileItem> GetFiles(
        FileFilterInput? filter,
        [Service] StrgDbContext db)
    {
        var query = db.Files.Where(f => f.IsInInbox);

        if (filter?.IsInInbox == false)
            query = db.Files.Where(f => !f.IsInInbox);  // allow overriding to non-inbox

        if (filter?.NameContains is not null)
            query = query.Where(f => f.Name.Contains(filter.NameContains));

        // ... apply remaining FileFilterInput fields

        return query.OrderByDescending(f => f.InboxEnteredAt);
    }

    [UsePaging(DefaultPageSize = 20, MaxPageSize = 200)]
    [GraphQLComplexity(5)]
    [Authorize]
    public IQueryable<InboxRuleExecutionLog> GetExecutionLogs(
        ID? fileId, ID? ruleId,
        [Service] StrgDbContext db)
    {
        var query = db.InboxRuleExecutionLogs.AsQueryable();
        if (fileId.HasValue) query = query.Where(l => l.FileId == (Guid)fileId);
        if (ruleId.HasValue) query = query.Where(l => l.RuleId == (Guid)ruleId);
        return query.OrderByDescending(l => l.EvaluatedAt);
    }
}
```

### DataLoaders used:

- `InboxRuleByIdDataLoader` — batch-loads rules for execution log entries
- `FileItemByIdDataLoader` — batch-loads files for execution log entries (reuse from STRG-049)

### `InboxRuleExecutionLogType` descriptor:

```csharp
public sealed class InboxRuleExecutionLogType : ObjectType<InboxRuleExecutionLog>
{
    protected override void Configure(IObjectTypeDescriptor<InboxRuleExecutionLog> descriptor)
    {
        descriptor.ImplementsNode().IdField(l => l.Id);
        descriptor.Field(l => l.TenantId).Ignore();

        descriptor.Field("file")
            .ResolveWith<InboxLogResolvers>(r => r.GetFile(default!, default!, default!));

        descriptor.Field("rule")
            .ResolveWith<InboxLogResolvers>(r => r.GetRule(default!, default!, default!));
    }
}
```

### `FileItem` inbox fields (added in FileItemType from STRG-050):

```graphql
# These fields are already on FileItem; confirm they are visible in schema:
type FileItem {
  isInInbox: Boolean!
  inboxStatus: InboxStatus
}

enum InboxStatus { PENDING PROCESSED FAILED }
```

### Usage examples:

```graphql
query {
  inbox {
    rules(first: 20) {
      nodes { id name priority isEnabled conditionTree actions }
      pageInfo { hasNextPage }
      totalCount
    }
  }
}

query {
  inbox {
    files(first: 10) {
      nodes { id name mimeType isInInbox inboxStatus inboxEnteredAt }
      pageInfo { hasNextPage }
    }
  }
}

query {
  inbox {
    executionLogs(first: 10, fileId: "...") {
      nodes {
        id matched evaluatedAt actionsTaken
        file { id name }
        rule { id name }
      }
      pageInfo { hasNextPage }
      totalCount
    }
  }
}
```

## Acceptance Criteria

- [ ] `query { inbox { rules(first: 20) { nodes { id name priority } pageInfo { hasNextPage } totalCount } } }` → cursor-paginated rules
- [ ] `query { inbox { rule(id: "...") { id name } } }` → single rule or `null`
- [ ] `query { inbox { files(first: 10) { nodes { id isInInbox inboxStatus } } } }` → only inbox files
- [ ] `query { inbox { executionLogs(fileId: "...", first: 10) { nodes { matched rule { name } } } } }` → cursor-paginated logs
- [ ] Execution log `rule` and `file` resolved via DataLoaders (no N+1)
- [ ] All queries respect tenant isolation (global query filter)
- [ ] All queries require authentication

## Test Cases

- TC-001: `rules` returns only the calling user's rules in priority order
- TC-002: `files` returns only files where `IsInInbox = true`
- TC-003: `executionLogs(fileId: "x")` returns all log entries for that file
- TC-004: Log entry `rule` resolved via DataLoader (assert query count)
- TC-005: `rules(first: 5, after: "cursor")` → second page correct
- TC-006: Rule from another user not visible in `rules` query

## Implementation Tasks

- [ ] Create `InboxQueries` marker record + `InboxRuleQueries` extension in `src/Strg.GraphQL/Queries/`
- [ ] Create `InboxRuleExecutionLogType.cs` in `src/Strg.GraphQL/Types/`
- [ ] Create `InboxRuleByIdDataLoader.cs` in `src/Strg.GraphQL/DataLoaders/`
- [ ] Extend `FileItemType` with inbox fields (if not already done in STRG-050)
- [ ] Types auto-discovered by `AddTypes()` — no manual registration

## Security Review Checklist

- [ ] `TenantId` ignored in `InboxRuleExecutionLogType`
- [ ] User can only see their own rules (userId filter in resolver)
- [ ] No cross-tenant data in execution logs (global query filter + resolver check)

## Definition of Done

- [ ] All queries return Relay cursor-paginated connections
- [ ] N+1 prevention verified via DataLoader assertion in integration tests
