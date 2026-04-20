---
id: STRG-055
title: Implement audit log GraphQL query
milestone: v0.1
priority: medium
status: done
type: implementation
labels: [graphql, audit, admin]
depends_on: [STRG-049, STRG-062]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-055: Implement audit log GraphQL query

## Summary

Implement the `auditLog` query under the `admin` namespace, accessible only to Admin users. Returns cursor-paginated audit log entries with handcrafted filtering. Audit entries are append-only and never soft-deleted.

## Technical Specification

### Schema (under `query { admin { ... } }`):

```graphql
type AdminQueries {
  auditLog(
    first: Int
    after: String
    last: Int
    before: String
    filter: AuditFilterInput
  ): AuditEntryConnection!
}

type AuditEntry implements Node {
  id: ID!
  action: String!
  resourceType: String!
  resourceId: ID!
  performedAt: DateTime!
  performedBy: User!   # resolved via UserByIdDataLoader
  metadata: JSON
  # ipAddress exposed only to admin — field is already admin-gated by AdminQueries
}

# Handcrafted filter — not HC auto-generated
input AuditFilterInput {
  userId: ID
  action: String
  resourceType: String
  from: DateTime
  to: DateTime
}
```

### File: `src/Strg.GraphQL/Queries/AdminQueries.cs`

```csharp
public sealed record AdminQueries;  // namespace marker

[ExtendObjectType<AdminQueries>]
public sealed class AuditLogQueries
{
    [UsePaging(DefaultPageSize = 50, MaxPageSize = 500)]
    [GraphQLComplexity(5)]
    public IQueryable<AuditEntry> GetAuditLog(
        AuditFilterInput? filter,
        [Service] StrgDbContext db,
        [GlobalState("tenantId")] Guid tenantId)
    {
        var query = db.AuditEntries
            .Where(e => e.TenantId == tenantId)  // explicit — AuditEntry has no global tenant filter
            .OrderByDescending(e => e.PerformedAt);

        if (filter?.UserId is not null)
            query = query.Where(e => e.UserId == (Guid)filter.UserId);
        if (filter?.Action is not null)
            query = query.Where(e => e.Action == filter.Action);
        if (filter?.ResourceType is not null)
            query = query.Where(e => e.ResourceType == filter.ResourceType);
        if (filter?.From.HasValue == true)
            query = query.Where(e => e.PerformedAt >= filter.From);
        if (filter?.To.HasValue == true)
            query = query.Where(e => e.PerformedAt <= filter.To);

        return query;
    }
}
```

### File: `src/Strg.GraphQL/Types/AuditEntryType.cs`

```csharp
public sealed class AuditEntryType : ObjectType<AuditEntry>
{
    protected override void Configure(IObjectTypeDescriptor<AuditEntry> descriptor)
    {
        descriptor.ImplementsNode().IdField(e => e.Id);
        descriptor.Field(e => e.TenantId).Ignore();
        descriptor.Field(e => e.UserId).Ignore();  // exposed via performedBy: User! via DataLoader

        descriptor.Field("performedBy")
            .ResolveWith<AuditEntryResolvers>(r => r.GetPerformedBy(default!, default!, default!));
    }
}
```

### Important: AuditEntry has no global query filter

`AuditEntry` deliberately has no soft-delete filter and no global tenant filter — admin can query the full audit log for their tenant. The `tenantId` filter is applied explicitly in the resolver.

### Usage example:

```graphql
query {
  admin {
    auditLog(first: 20, filter: { action: "file.deleted", from: "2026-01-01" }) {
      nodes {
        id
        action
        resourceType
        performedAt
        performedBy { email displayName }
        metadata
      }
      pageInfo { hasNextPage endCursor }
      totalCount
    }
  }
}
```

## Acceptance Criteria

- [ ] `query { admin { auditLog { nodes { action performedAt } pageInfo { hasNextPage } totalCount } } }` → paginated audit entries
- [ ] Non-admin → `UNAUTHORIZED` (enforced at `AdminQueries` namespace field in RootQueryExtension)
- [ ] `filter: { action: "file.deleted" }` → filtered results
- [ ] `filter: { from: "2026-01-01", to: "2026-04-20" }` → time-range filtered
- [ ] `AuditEntry.metadata` returned as opaque JSON object
- [ ] `performedBy` resolved via `UserByIdDataLoader` (no N+1)
- [ ] `MaxPageSize` 500 for admin audit queries (higher than regular 200)

## Test Cases

- **TC-001**: Upload file → audit entry appears in `admin { auditLog }` query
- **TC-002**: Non-admin query → `UNAUTHORIZED`
- **TC-003**: `filter: { action: "file.deleted" }` → only matching entries
- **TC-004**: Pagination `first: 10` → 10 entries, `pageInfo.hasNextPage` correct
- **TC-005**: `performedBy` fields resolve without extra DB queries (DataLoader assertion)

## Implementation Tasks

- [ ] Create `AdminQueries.cs` marker record + `AuditLogQueries` extension in `src/Strg.GraphQL/Queries/`
- [ ] Create `AuditEntryType.cs` in `src/Strg.GraphQL/Types/`
- [ ] Create `UserByIdDataLoader.cs` in `src/Strg.GraphQL/DataLoaders/`
- [ ] Create `AuditFilterInput.cs` record in `src/Strg.GraphQL/Inputs/`
- [ ] Configure `AuditEntry` EF Core mapping without tenant global filter (STRG-062)
- [ ] Types auto-discovered by `AddTypes()` — no manual registration

## Security Review Checklist

- [ ] `Admin` policy enforced at the `Admin()` namespace field in `RootQueryExtension`
- [ ] `TenantId` filter applied explicitly (no cross-tenant audit leak)
- [ ] `TenantId` field ignored in `AuditEntryType`

## Code Review Checklist

- [ ] `AuditEntry` query does NOT use global tenant filter (comment explains why)
- [ ] `MaxPageSize` set higher for admin queries (500 vs 200 for regular)
- [ ] `performedBy` uses DataLoader, not individual DB query

## Definition of Done

- [ ] Admin can query audit log with Relay cursor pagination and handcrafted filtering
- [ ] N+1 prevention verified via DataLoader
