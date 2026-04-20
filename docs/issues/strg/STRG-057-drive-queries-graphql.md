---
id: STRG-057
title: Implement Drive GraphQL queries (getDrives, getDrive)
milestone: v0.1
priority: high
status: open
type: implementation
labels: [graphql, drives, api]
depends_on: [STRG-049, STRG-050, STRG-025]
blocks: [STRG-053]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-057: Implement Drive GraphQL queries

## Summary

Implement `drives` and `drive(id)` GraphQL queries. Returns all drives accessible to the authenticated user within their tenant. `Drive.providerConfig` is never exposed.

## Technical Specification

### Schema:

```graphql
type Query {
  drives: [Drive!]!
  drive(id: UUID!): Drive
}
```

### File: `src/Strg.GraphQL/Queries/DriveQueries.cs`

```csharp
[ExtendObjectType("Query")]
public class DriveQueries
{
    [Authorize(Policy = "FilesRead")]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<Drive> GetDrives(
        [Service] StrgDbContext db,
        [GlobalState("tenantId")] Guid tenantId)
        => db.Drives.Where(d => d.TenantId == tenantId);

    [Authorize(Policy = "FilesRead")]
    public Task<Drive?> GetDrive(
        Guid id,
        [Service] StrgDbContext db,
        [GlobalState("tenantId")] Guid tenantId,
        CancellationToken ct)
        => db.Drives.FirstOrDefaultAsync(
               d => d.Id == id && d.TenantId == tenantId, ct);
}
```

### `DriveType` (from STRG-050, extended here):

```csharp
public class DriveType : ObjectType<Drive>
{
    protected override void Configure(IObjectTypeDescriptor<Drive> descriptor)
    {
        descriptor.Field(d => d.ProviderConfig).Ignore();
        descriptor.Field(d => d.TenantId).Ignore();

        // Include nested files with pagination
        descriptor.Field(d => d.Files)
            .UseProjection()
            .UseFiltering()
            .UseSorting()
            .UsePaging<ObjectType<FileItem>>(options: new PagingOptions
            {
                DefaultPageSize = 50,
                MaxPageSize = 200
            });
    }
}
```

## Acceptance Criteria

- [ ] `query { drives { id name providerType encryptionEnabled } }` → all tenant drives
- [ ] `query { drive(id: "...") { id name } }` → single drive or `null`
- [ ] `Drive.providerConfig` NOT in schema (introspection test)
- [ ] `Drive.tenantId` NOT in schema
- [ ] Drive from different tenant → `null` (not error)
- [ ] `Drive.files` supports pagination, filtering, sorting (nested connection)

## Test Cases

- **TC-001**: Query `drives` → only current tenant's drives returned
- **TC-002**: Query `drive(id: "other-tenant-drive")` → `null`
- **TC-003**: Introspect `Drive` type → `providerConfig` field absent
- **TC-004**: `drives { files(first: 5) { nodes { name } } }` → paginated files

## Implementation Tasks

- [ ] Create `DriveQueries.cs` in `Strg.GraphQL/Queries/`
- [ ] Update `DriveType.cs` to configure nested files pagination
- [ ] Register `DriveQueries` in Hot Chocolate setup (STRG-049)

## Testing Tasks

- [ ] Integration test: `drives` query returns only current tenant drives
- [ ] Integration test: `providerConfig` absent from schema
- [ ] Integration test: nested `files` pagination works

## Security Review Checklist

- [ ] `providerConfig` ignored (may contain storage credentials)
- [ ] Tenant filter applied in resolver (not relying solely on global filter)

## Code Review Checklist

- [ ] `GetDrive` returns `null`, not throws, for inaccessible drives
- [ ] `tenantId` filter explicit in query (belt-and-suspenders with global filter)

## Definition of Done

- [ ] Drive list and single-drive queries working
- [ ] `providerConfig` absent from schema confirmed
