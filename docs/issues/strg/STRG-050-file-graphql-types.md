---
id: STRG-050
title: Create FileItemType, DriveType, and file listing queries
milestone: v0.1
priority: critical
status: done
type: implementation
labels: [graphql, api, files]
depends_on: [STRG-049, STRG-025, STRG-031]
blocks: [STRG-052, STRG-053]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-050: Create FileItemType, DriveType, and file listing queries

## Summary

Create Hot Chocolate GraphQL descriptor types for `Drive` and `FileItem`, and wire up file listing and single-file queries under the `StorageQueries` namespace. Uses handcrafted `FileFilterInput` (not HC auto-generated filtering) and Relay cursor pagination everywhere.

## Technical Specification

### File: `src/Strg.GraphQL/Types/DriveType.cs`

```csharp
public sealed class DriveType : ObjectType<Drive>
{
    protected override void Configure(IObjectTypeDescriptor<Drive> descriptor)
    {
        descriptor.ImplementsNode().IdField(d => d.Id);
        descriptor.Field(d => d.ProviderConfig).Ignore();  // never expose credentials
        descriptor.Field(d => d.TenantId).Ignore();
        descriptor.Field(d => d.IsEncrypted);
        descriptor.Field(d => d.IsDefault);
        descriptor.Field("files")
            .Argument("first", a => a.Type<IntType>())
            .Argument("after", a => a.Type<StringType>())
            .Argument("path", a => a.Type<StringType>())
            .Argument("filter", a => a.Type<FileFilterInputType>())
            .UsePaging<ObjectType<FileItem>>(options: new PagingOptions
            {
                DefaultPageSize = 50, MaxPageSize = 200
            })
            .ResolveWith<DriveResolvers>(r => r.GetFiles(default!, default!, default!, default!));
    }
}
```

### File: `src/Strg.GraphQL/Types/FileItemType.cs`

```csharp
public sealed class FileItemType : ObjectType<FileItem>
{
    protected override void Configure(IObjectTypeDescriptor<FileItem> descriptor)
    {
        descriptor.ImplementsNode().IdField(f => f.Id);
        descriptor.Field(f => f.TenantId).Ignore();
        descriptor.Field(f => f.IsFolder);       // isFolder: Boolean! (not isDirectory)
        descriptor.Field(f => f.MimeType);       // null for folders
        descriptor.Field(f => f.Size);           // null for folders
        descriptor.Field(f => f.IsInInbox);
        descriptor.Field(f => f.InboxStatus);
        descriptor.Field("children")
            .UsePaging<ObjectType<FileItem>>(options: new PagingOptions { DefaultPageSize = 50, MaxPageSize = 200 })
            .ResolveWith<FileItemResolvers>(r => r.GetChildren(default!, default!, default!));
        descriptor.Field("tags")
            .UsePaging<ObjectType<Tag>>(options: new PagingOptions { DefaultPageSize = 100, MaxPageSize = 500 })
            .ResolveWith<FileItemResolvers>(r => r.GetTags(default!, default!, default!));
        descriptor.Field("versions")
            .UsePaging<ObjectType<FileVersion>>(options: new PagingOptions { DefaultPageSize = 20, MaxPageSize = 100 })
            .ResolveWith<FileItemResolvers>(r => r.GetVersions(default!, default!, default!));
    }
}
```

### File: `src/Strg.GraphQL/Queries/FileQueries.cs`

```csharp
[ExtendObjectType<StorageQueries>]
public sealed class FileQueries
{
    [UsePaging(DefaultPageSize = 50, MaxPageSize = 200)]
    [GraphQLComplexity(5)]
    [Authorize(Policy = "FilesRead")]
    public IQueryable<FileItem> GetFiles(
        ID driveId,
        string? path,
        FileFilterInput? filter,
        [Service] StrgDbContext db)
    {
        var query = db.Files.Where(f => f.DriveId == (Guid)driveId);

        if (path is not null) query = query.Where(f => f.Path.StartsWith(path));
        if (filter?.NameContains is not null) query = query.Where(f => f.Name.Contains(filter.NameContains));
        if (filter?.MimeType is not null) query = ApplyMimeTypeFilter(query, filter.MimeType);
        if (filter?.IsFolder.HasValue == true) query = query.Where(f => f.IsFolder == filter.IsFolder);
        if (filter?.MinSize.HasValue == true) query = query.Where(f => f.Size >= filter.MinSize);
        if (filter?.MaxSize.HasValue == true) query = query.Where(f => f.Size <= filter.MaxSize);
        if (filter?.CreatedAfter.HasValue == true) query = query.Where(f => f.CreatedAt >= filter.CreatedAfter);
        if (filter?.CreatedBefore.HasValue == true) query = query.Where(f => f.CreatedAt <= filter.CreatedBefore);
        if (filter?.IsInInbox.HasValue == true) query = query.Where(f => f.IsInInbox == filter.IsInInbox);

        return query;
    }

    [Authorize(Policy = "FilesRead")]
    public Task<FileItem?> GetFile(
        ID id,
        [Service] StrgDbContext db,
        CancellationToken ct)
        => db.Files.FirstOrDefaultAsync(f => f.Id == (Guid)id, ct);
}
```

### Schema (SDL):

```graphql
type Drive implements Node {
  id: ID!
  name: String!
  providerType: String!
  isDefault: Boolean!
  isEncrypted: Boolean!
  createdAt: DateTime!
  files(first: Int, after: String, path: String, filter: FileFilterInput): FileItemConnection!
  # ProviderConfig, TenantId — never exposed
}

type FileItem implements Node {
  id: ID!
  name: String!
  path: String!
  isFolder: Boolean!
  mimeType: String
  size: Long
  createdAt: DateTime!
  updatedAt: DateTime!
  deletedAt: DateTime
  isInInbox: Boolean!
  inboxStatus: InboxStatus
  drive: Drive!
  parent: FileItem
  children(first: Int, after: String): FileItemConnection
  tags(first: Int, after: String): TagConnection!
  versions(first: Int, after: String): FileVersionConnection
  latestVersion: FileVersion
}

# Queries under storage namespace:
# query { storage { files(driveId: "...", filter: {...}) { nodes { id name } pageInfo { hasNextPage } totalCount } } }
# query { storage { file(id: "...") { id name isFolder } } }
```

### Handcrafted filter input (no HC auto-generated filtering):

```graphql
input FileFilterInput {
  nameContains: String
  mimeType: String      # supports wildcard "image/*"
  isFolder: Boolean
  minSize: Long
  maxSize: Long
  createdAfter: DateTime
  createdBefore: DateTime
  tagKey: String
  isInInbox: Boolean
}

input FileSortInput {
  field: FileSortField!
  direction: SortDirection!
}
enum FileSortField  { NAME  SIZE  CREATED_AT  UPDATED_AT  MIME_TYPE }
enum SortDirection  { ASC  DESC }
```

## Acceptance Criteria

- [ ] `query { storage { drives { nodes { id name } } } }` returns the user's accessible drives
- [ ] `query { storage { files(driveId: "...") { nodes { id name size } pageInfo { hasNextPage } totalCount } } }` returns paginated files
- [ ] `query { storage { files(driveId: "...", filter: { nameContains: "report" }) { nodes { id name } } } }` filters correctly
- [ ] `query { storage { files(driveId: "...", filter: { mimeType: "image/*" }) { nodes { id } } } }` wildcard mime filter works
- [ ] `Drive.providerConfig` is NOT in schema
- [ ] `Drive.tenantId` is NOT in schema
- [ ] `FileItem.isFolder` present (not `isDirectory`)
- [ ] Pagination uses Relay cursor spec (`nodes`, `pageInfo`, `totalCount`)
- [ ] `query { storage { file(id: "...") { ... } } }` returns `null` for inaccessible files (not error)

## Test Cases

- **TC-001**: Query drives → only drives accessible to user returned
- **TC-002**: Query files with `filter: { nameContains: "report" }` → filtered results
- **TC-003**: Query files with `first: 10, after: cursor` → second page of 10 files
- **TC-004**: Query `file(id: "...")` for another user's private file → `null`
- **TC-005**: `Drive.providerConfig` in introspection → field not visible
- **TC-006**: `FileItem.isFolder` returns `true` for folders, `false` for files
- **TC-007**: `FileItem.mimeType` is `null` for folders

## Implementation Tasks

- [ ] Create `DriveType.cs` in `src/Strg.GraphQL/Types/`
- [ ] Create `FileItemType.cs` in `src/Strg.GraphQL/Types/`
- [ ] Create `FileQueries.cs` in `src/Strg.GraphQL/Queries/` with `[ExtendObjectType<StorageQueries>]`
- [ ] Create `FileFilterInput.cs` record in `src/Strg.GraphQL/Inputs/`
- [ ] Create `FileSortInput.cs` record in `src/Strg.GraphQL/Inputs/`
- [ ] Types are auto-discovered by `AddTypes()` in STRG-049 — no manual registration

## Security Review Checklist

- [ ] `ProviderConfig` field ignored in `DriveType` (contains credentials)
- [ ] `TenantId` field ignored in all types
- [ ] File queries filtered by tenant (global query filter handles this)
- [ ] `isFolder` used (not `isDirectory`) to match domain model field name

## Definition of Done

- [ ] File listing query works with Relay cursor pagination and handcrafted filtering
- [ ] Security-sensitive fields confirmed absent from schema
