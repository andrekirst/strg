---
id: STRG-055
title: Implement audit log GraphQL query
milestone: v0.1
priority: medium
status: open
type: implementation
labels: [graphql, audit, admin]
depends_on: [STRG-049, STRG-062]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-055: Implement audit log GraphQL query

## Summary

Implement `auditLog` GraphQL query accessible only to Admin users. Returns paginated audit log entries with filtering by user, action type, resource, and time range. Audit entries are append-only and never soft-deleted.

## Technical Specification

### Schema:

```graphql
type Query {
  auditLog(
    first: Int
    after: String
    where: AuditEntryFilterInput
    order: [AuditEntrySortInput!]
  ): AuditEntryConnection!
}

type AuditEntry {
  id: UUID!
  userId: UUID!
  action: String!
  resourceId: UUID!
  resourceType: String!
  ipAddress: String
  metadata: JSON
  timestamp: DateTime!
}

input AuditEntryFilterInput {
  userId: UuidOperationFilterInput
  action: StringOperationFilterInput
  resourceId: UuidOperationFilterInput
  timestamp: DateTimeOperationFilterInput
}
```

### File: `src/Strg.GraphQL/Queries/AuditQueries.cs`

```csharp
[ExtendObjectType("Query")]
public class AuditQueries
{
    [Authorize(Policy = "Admin")]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    [UsePaging(DefaultPageSize = 50, MaxPageSize = 500)]
    public IQueryable<AuditEntry> GetAuditLog(
        [Service] StrgDbContext db,
        [GlobalState("tenantId")] Guid tenantId)
        => db.AuditEntries
             .Where(e => e.TenantId == tenantId)
             .OrderByDescending(e => e.Timestamp);
}
```

### Important: no global query filter on `AuditEntry`

The `AuditEntry` entity deliberately has no soft-delete filter and no global tenant filter (so that admin can query across the full audit log). The `tenantId` filter is applied explicitly in the resolver.

## Acceptance Criteria

- [ ] `query { auditLog { nodes { action timestamp userId } } }` → paginated audit entries
- [ ] Non-admin → `UNAUTHORIZED` GraphQL error
- [ ] `where: { action: { eq: "file.deleted" } }` → filtered results
- [ ] `order: [{ timestamp: DESC }]` → newest first (default)
- [ ] `AuditEntry.metadata` returned as opaque JSON object (not parsed)
- [ ] Admin can filter by `userId` to audit a specific user's actions

## Test Cases

- **TC-001**: Upload file → audit entry appears in `auditLog` query
- **TC-002**: Non-admin query → `UNAUTHORIZED`
- **TC-003**: `where: { action: { contains: "file." } }` → only file-related entries
- **TC-004**: Pagination: `first: 10` → 10 entries, `pageInfo.hasNextPage` correct

## Implementation Tasks

- [ ] Create `AuditQueries.cs` in `Strg.GraphQL/Queries/`
- [ ] Create `AuditEntryType.cs` (exposes `metadata` as `JSON` scalar)
- [ ] Configure `AuditEntry` in EF Core without tenant global filter (STRG-062)
- [ ] Register types in Hot Chocolate setup

## Testing Tasks

- [ ] Integration test: perform operations → verify in audit log query
- [ ] Integration test: non-admin → UNAUTHORIZED

## Security Review Checklist

- [ ] `Admin` policy required
- [ ] `TenantId` filter applied explicitly (no cross-tenant audit leak)
- [ ] `ipAddress` visible to admin only (already Admin-gated)

## Code Review Checklist

- [ ] `AuditEntry` query does NOT use global tenant filter (admin can see all in their tenant)
- [ ] `MaxPageSize` higher for admin queries (500 vs 200 for regular queries)

## Definition of Done

- [ ] Admin can query audit log with pagination and filtering
