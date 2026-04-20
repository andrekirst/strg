---
id: STRG-050
title: Create FileType, DriveType, and file listing GraphQL queries
milestone: v0.1
priority: critical
status: open
type: implementation
labels: [graphql, api, files]
depends_on: [STRG-049, STRG-025, STRG-031]
blocks: [STRG-052, STRG-053]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-050: Create FileType, DriveType, and file listing GraphQL queries

## Summary

Create Hot Chocolate GraphQL types for `Drive` and `FileItem`, and wire up the file listing and single-file queries with filtering, sorting, and cursor pagination.

## Technical Specification

### File: `src/Strg.GraphQL/Types/DriveType.cs`

```csharp
public class DriveType : ObjectType<Drive>
{
    protected override void Configure(IObjectTypeDescriptor<Drive> descriptor)
    {
        descriptor.Field(d => d.ProviderConfig).Ignore(); // never expose raw config
        descriptor.Field(d => d.TenantId).Ignore();
    }
}
```

### File: `src/Strg.GraphQL/Types/FileType.cs`

```csharp
[ExtendObjectType("Query")]
public class FileQueries
{
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    [UsePaging(DefaultPageSize = 50, MaxPageSize = 200)]
    [Authorize(Policy = "FilesRead")]
    public IQueryable<FileItem> GetFiles(
        [Service] StrgDbContext db,
        [GlobalState("userId")] Guid userId,
        Guid driveId)
        => db.Files.Where(f => f.DriveId == driveId);

    [Authorize(Policy = "FilesRead")]
    public Task<FileItem?> GetFile(
        [Service] StrgDbContext db,
        Guid id)
        => db.Files.FirstOrDefaultAsync(f => f.Id == id);
}
```

### Exposed GraphQL schema:

```graphql
type Drive {
  id: UUID!
  name: String!
  providerType: String!
  encryptionEnabled: Boolean!
  createdAt: DateTime!
  files(first: Int, after: String, where: FileFilterInput, order: [FileSortInput!]): FileConnection!
}

type FileItem {
  id: UUID!
  name: String!
  path: String!
  size: Long!
  contentHash: String
  isDirectory: Boolean!
  mimeType: String!
  versionCount: Int!
  createdAt: DateTime!
  updatedAt: DateTime!
  tags: [Tag!]!
  drive: Drive!
}
```

## Acceptance Criteria

- [ ] `query { drives { id name } }` returns the user's accessible drives
- [ ] `query { files(driveId: "...") { nodes { id name size } } }` returns paginated files
- [ ] `query { files(driveId: "...", where: { name: { contains: "report" } }) }` filters correctly
- [ ] `query { files(driveId: "...", order: [{ name: ASC }]) }` sorts correctly
- [ ] `Drive.providerConfig` is NOT exposed in schema
- [ ] `Drive.tenantId` is NOT exposed
- [ ] Pagination uses Relay cursor spec (`edges`, `node`, `cursor`, `pageInfo`)
- [ ] `FileItem.tags` returns only the requesting user's tags
- [ ] `query { file(id: "...") { ... } }` returns `null` for inaccessible files (not error)

## Test Cases

- **TC-001**: Query drives → only drives accessible to user returned
- **TC-002**: Query files with `where: { name: { contains: "report" } }` → filtered results
- **TC-003**: Query files with `first: 10, after: cursor` → second page of 10 files
- **TC-004**: Query `file(id: "...")` for another user's private file → `null`
- **TC-005**: `Drive.providerConfig` in introspection → field not visible

## Implementation Tasks

- [ ] Create `DriveType.cs`
- [ ] Create `FileType.cs`
- [ ] Create `FileQueries.cs` with `GetFiles` and `GetFile`
- [ ] Create `DriveQueries.cs` with `GetDrives` and `GetDrive`
- [ ] Register types in Hot Chocolate setup (STRG-049)
- [ ] Write integration tests using Hot Chocolate test client

## Security Review Checklist

- [ ] `providerConfig` field ignored in DriveType (may contain credentials)
- [ ] `tenantId` field ignored in all types
- [ ] File queries filtered by tenant (global query filter handles this)
- [ ] User can only see files they have read access to

## Definition of Done

- [ ] File listing query works with pagination, filtering, and sorting
- [ ] Security sensitive fields confirmed absent from schema
