# GraphQL Interface Design

**Date:** 2026-04-20  
**Status:** Approved  
**Scope:** Core (STRG-049–066) + Inbox (STRG-310–312)

---

## Context

This document records the agreed design for the strg GraphQL API. It governs all issues in the STRG-049 through STRG-066 and STRG-310 through STRG-312 range. Implementers must follow these decisions; deviations require a design revision.

---

## Decisions Summary

| Decision | Choice |
|---|---|
| Schema style | Code-first with `ObjectType<T>` descriptor classes |
| HC version | 15.x (never 16.x preview) |
| Mutation results | Relay-style payload `{ entity  errors: [UserError!] }` |
| Pagination | Relay cursor everywhere (`Connection` / `nodes` / `pageInfo`) |
| Query organisation | Namespaced field objects (`storage`, `inbox`, `admin`) |
| Mutation organisation | Namespaced parallel to queries |
| File/folder | Single `FileItem` type with `isFolder: Boolean!` |
| Relay compliance | Full `Node` interface + `node(id: ID!)` query |
| HC registration | Assembly scanning via `AddTypes(assembly)` — OCP |
| Subscription backplane | Redis (prod) / in-memory (dev + tests) |
| Filtering | Handcrafted input types — no HC auto-generated filter types |

---

## Schema Root

```graphql
interface Node { id: ID! }

type Query {
  node(id: ID!): Node                   # Relay global refetch
  me: User!                             # current user from JWT sub claim
  storage: StorageQueries!
  inbox: InboxQueries!
  admin: AdminQueries!                  # requires admin scope
}

type StorageQueries {
  drives(first: Int, after: String, last: Int, before: String): DriveConnection!
  drive(id: ID!): Drive
  files(driveId: ID!, path: String, first: Int, after: String, last: Int, before: String, filter: FileFilterInput, order: [FileSortInput!]): FileItemConnection!
  file(id: ID!): FileItem
}

type InboxQueries {
  rules(first: Int, after: String, last: Int, before: String): InboxRuleConnection!
  rule(id: ID!): InboxRule
  files(first: Int, after: String, last: Int, before: String, filter: FileFilterInput): FileItemConnection!
}

type AdminQueries {
  auditLog(first: Int, after: String, last: Int, before: String, filter: AuditFilterInput): AuditEntryConnection!
  users(first: Int, after: String): UserConnection!
  user(id: ID!): User
}

type Mutation {
  storage: StorageMutations!
  inbox: InboxMutations!
  user: UserMutations!
  admin: AdminMutations!
}

type Subscription {
  fileEvents(driveId: ID!): FileEvent!
  inboxFileProcessed: InboxFileProcessedEvent!
}
```

---

## Entity Types

All entity types implement `Node`.

```graphql
type User implements Node {
  id: ID!
  email: String!
  displayName: String!
  quotaBytes: Long!
  usedBytes: Long!
  createdAt: DateTime!
  updatedAt: DateTime!
  # TenantId — NEVER exposed
}

type Drive implements Node {
  id: ID!
  name: String!
  providerType: String!
  isDefault: Boolean!
  isEncrypted: Boolean!
  createdAt: DateTime!
  files(first: Int, after: String, path: String, filter: FileFilterInput): FileItemConnection!
  # ProviderConfig — NEVER exposed (contains storage credentials)
}

type FileItem implements Node {
  id: ID!
  name: String!
  path: String!
  isFolder: Boolean!
  mimeType: String              # null for folders
  size: Long                    # null for folders
  createdAt: DateTime!
  updatedAt: DateTime!
  deletedAt: DateTime
  isInInbox: Boolean!
  inboxStatus: InboxStatus
  drive: Drive!
  parent: FileItem
  children(first: Int, after: String): FileItemConnection  # null for files
  tags(first: Int, after: String): TagConnection!
  versions(first: Int, after: String): FileVersionConnection  # null for folders
  latestVersion: FileVersion
}

type Tag implements Node {
  id: ID!
  key: String!
  value: String!
  valueType: TagValueType!
  createdAt: DateTime!
}

type FileVersion implements Node {
  id: ID!
  versionNumber: Int!
  size: Long!
  mimeType: String!
  createdAt: DateTime!
  createdBy: User!
}

type AuditEntry implements Node {
  id: ID!
  action: String!
  resourceType: String!
  resourceId: ID!
  performedAt: DateTime!
  performedBy: User!
  metadata: JSON
}

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

type InboxRuleExecutionLog implements Node {
  id: ID!
  file: FileItem!
  rule: InboxRule!
  matched: Boolean!
  actionsTaken: JSON!
  evaluatedAt: DateTime!
}

enum InboxStatus    { PENDING PROCESSED FAILED }
enum TagValueType   { STRING INT FLOAT BOOL DATETIME }
enum FileEventType  { UPLOADED DELETED MOVED COPIED RENAMED }
```

---

## Mutations

### Shared error type

```graphql
type UserError {
  code: String!
  message: String!
  field: String    # populated from ValidationException.PropertyName for form validation
}
```

### StorageMutations

```graphql
type StorageMutations {
  createDrive(input: CreateDriveInput!): CreateDrivePayload!
  updateDrive(input: UpdateDriveInput!): UpdateDrivePayload!
  deleteDrive(input: DeleteDriveInput!): DeleteDrivePayload!

  createFolder(input: CreateFolderInput!): CreateFolderPayload!
  deleteFile(input: DeleteFileInput!): DeleteFilePayload!
  moveFile(input: MoveFileInput!): MoveFilePayload!
  copyFile(input: CopyFileInput!): CopyFilePayload!
  renameFile(input: RenameFileInput!): RenameFilePayload!

  addTag(input: AddTagInput!): AddTagPayload!
  updateTag(input: UpdateTagInput!): UpdateTagPayload!
  removeTag(input: RemoveTagInput!): RemoveTagPayload!
  removeAllTags(input: RemoveAllTagsInput!): RemoveAllTagsPayload!
}

type CreateDrivePayload   { drive: Drive          errors: [UserError!] }
type UpdateDrivePayload   { drive: Drive          errors: [UserError!] }
type DeleteDrivePayload   { driveId: ID           errors: [UserError!] }
type CreateFolderPayload  { file: FileItem        errors: [UserError!] }
type DeleteFilePayload    { fileId: ID            errors: [UserError!] }
type MoveFilePayload      { file: FileItem        errors: [UserError!] }
type CopyFilePayload      { file: FileItem        errors: [UserError!] }
type RenameFilePayload    { file: FileItem        errors: [UserError!] }
type AddTagPayload        { tag: Tag              errors: [UserError!] }
type UpdateTagPayload     { tag: Tag              errors: [UserError!] }
type RemoveTagPayload     { tagId: ID             errors: [UserError!] }
type RemoveAllTagsPayload { fileId: ID            errors: [UserError!] }
```

### InboxMutations

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
```

### UserMutations

```graphql
type UserMutations {
  updateProfile(input: UpdateProfileInput!): UpdateProfilePayload!
  changePassword(input: ChangePasswordInput!): ChangePasswordPayload!
}

type UpdateProfilePayload  { user: User  errors: [UserError!] }
type ChangePasswordPayload { user: User  errors: [UserError!] }
```

### AdminMutations

```graphql
type AdminMutations {
  updateUserQuota(input: UpdateUserQuotaInput!): UpdateUserQuotaPayload!
  lockUser(input: LockUserInput!): LockUserPayload!
  unlockUser(input: UnlockUserInput!): UnlockUserPayload!
}

type UpdateUserQuotaPayload { user: User  errors: [UserError!] }
type LockUserPayload        { user: User  errors: [UserError!] }
type UnlockUserPayload      { user: User  errors: [UserError!] }
```

---

## Input Types

```graphql
# Drives
input CreateDriveInput  { name: String!  providerType: String!  providerConfig: JSON!  isDefault: Boolean  isEncrypted: Boolean }
input UpdateDriveInput  { id: ID!  name: String  isDefault: Boolean }
input DeleteDriveInput  { id: ID! }

# Files
input CreateFolderInput { driveId: ID!  path: String! }
input DeleteFileInput   { id: ID! }
input MoveFileInput     { id: ID!  targetPath: String!  conflictResolution: ConflictResolution }
input CopyFileInput     { id: ID!  targetPath: String!  conflictResolution: ConflictResolution }
input RenameFileInput   { id: ID!  newName: String! }

enum ConflictResolution { AUTO_RENAME  OVERWRITE  FAIL }

# Tags
input AddTagInput        { fileId: ID!  key: String!  value: String!  valueType: TagValueType! }
input UpdateTagInput     { id: ID!  value: String!  valueType: TagValueType! }
input RemoveTagInput     { id: ID! }
input RemoveAllTagsInput { fileId: ID! }

# User
input UpdateProfileInput  { displayName: String  email: String }
input ChangePasswordInput { currentPassword: String!  newPassword: String! }

# Admin
input UpdateUserQuotaInput { userId: ID!  quotaBytes: Long! }
input LockUserInput        { userId: ID!  reason: String }
input UnlockUserInput      { userId: ID! }

# InboxRule
input CreateInboxRuleInput    { name: String!  priority: Int!  conditionTree: JSON!  actions: JSON!  isEnabled: Boolean }
input UpdateInboxRuleInput    { id: ID!  name: String  priority: Int  conditionTree: JSON  actions: JSON  isEnabled: Boolean }
input DeleteInboxRuleInput    { id: ID! }
input DuplicateInboxRuleInput { id: ID!  newName: String }

# Filtering (handcrafted — not HC auto-generated)
input FileFilterInput {
  nameContains: String
  mimeType: String       # exact or wildcard "image/*"
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

input AuditFilterInput {
  userId: ID
  action: String
  resourceType: String
  from: DateTime
  to: DateTime
}
```

---

## Subscriptions

```graphql
type Subscription {
  fileEvents(driveId: ID!): FileEvent!
  inboxFileProcessed: InboxFileProcessedEvent!
}

type FileEvent {
  eventType: FileEventType!
  file: FileItem!
  driveId: ID!
  occurredAt: DateTime!
  # TenantId — NEVER exposed
}

type InboxFileProcessedEvent {
  file: FileItem!
  rulesEvaluated: Int!
  ruleMatched: InboxRule
  actionsTaken: [String!]!
  processedAt: DateTime!
}
```

**Topic naming:**
- `file-events:{driveId}` — per-drive file events
- `inbox-file-processed:{tenantId}` — per-tenant inbox events

**Backplane:**
- Production: Redis (`AddRedisSubscriptions`)
- Development / tests: in-memory (`AddInMemorySubscriptions`)

---

## HC Server Registration (OCP Pattern)

```csharp
// IGraphQLMarker is an empty interface in Strg.GraphQL — assembly anchor only
internal interface IGraphQLMarker { }

// Program.cs — never needs editing when adding new types
builder.Services.AddGraphQLServer()
    .AddQueryType(q => q.Name("Query"))
    .AddMutationType(m => m.Name("Mutation"))
    .AddSubscriptionType(s => s.Name("Subscription"))
    .AddTypes(typeof(IGraphQLMarker).Assembly)   // discovers all types/extensions
    .AddGlobalObjectIdentification()
    .AddFiltering()
    .AddSorting()
    .AddAuthorization()
    .RegisterDbContext<StrgDbContext>(DbContextKind.Pooled)
    .AddErrorFilter<StrgErrorFilter>()
    .AddMaxExecutionDepthRule(10)
    .ModifyRequestOptions(o => o.Complexity.MaximumAllowed = 100);

// Backplane (environment-driven)
if (env.IsDevelopment())
    builder.Services.AddGraphQLServer().AddInMemorySubscriptions();
else
    builder.Services.AddGraphQLServer()
        .AddRedisSubscriptions(sp =>
            ConnectionMultiplexer.Connect(
                sp.GetRequiredService<IConfiguration>()["Redis:ConnectionString"]!));
```

Each type self-registers via its attribute — no central file needs editing when adding a new query, mutation, or type:

```csharp
[ExtendObjectType<StorageQueries>]
public sealed class FileQueries { ... }

[ExtendObjectType<StorageMutations>]
public sealed class FileMutations { ... }

public sealed class FileItemType : ObjectType<FileItem> { ... }
```

---

## Namespace Resolver Pattern

Root namespace fields return marker objects; sub-fields live on extension types:

```csharp
[ExtendObjectType("Query")]
public sealed class RootQueryExtension
{
    public StorageQueries Storage() => new();
    public InboxQueries Inbox() => new();
    public AdminQueries Admin() => new();   // [Authorize(Policy = "Admin")] here
}

public sealed record StorageQueries;   // marker — no fields; extension types add them
public sealed record InboxQueries;
public sealed record AdminQueries;

[ExtendObjectType<StorageQueries>]
public sealed class DriveQueries
{
    [UsePaging] [Authorize(Policy = "FilesRead")]
    public IQueryable<Drive> GetDrives([Service] StrgDbContext db) => db.Drives;
}
```

---

## Security Rules

1. `ProviderConfig` — ignored in `DriveType` descriptor, never returned
2. `TenantId` — ignored in all descriptor classes, never returned
3. Stack traces — `StrgErrorFilter` removes them in production; `INTERNAL_ERROR` code only
4. User ID — always from `[GlobalState("userId")]` (JWT `sub` claim), never from mutation input
5. Tenant isolation — EF Core global query filter is the primary guard; resolvers add explicit filter as belt-and-suspenders
6. Path safety — all user-supplied paths through `StoragePath.Parse()` before reaching `IStorageProvider`
7. Introspection — disabled in production via `ModifyOptions(o => o.EnableSchemaIntrospection = false)`
8. Depth limit — 10 levels max
9. Complexity limit — 100 units max; paged list resolvers annotated with `[GraphQLComplexity(5)]`

---

## Error Handling

`StrgErrorFilter` maps domain exceptions to `extensions.code`:

| Exception | Code |
|---|---|
| `StoragePathException` | `INVALID_PATH` |
| `ValidationException` | `VALIDATION_ERROR` |
| `UnauthorizedAccessException` | `FORBIDDEN` |
| `NotFoundException` | `NOT_FOUND` |
| `QuotaExceededException` | `QUOTA_EXCEEDED` |
| `DuplicateDriveNameException` | `DUPLICATE_DRIVE_NAME` |
| Any other | `INTERNAL_ERROR` |

Mutation payload `errors` array contains `UserError { code message field }`. The `field` property is populated from `ValidationException.PropertyName` to enable precise form-field highlighting in clients.

---

## DataLoaders (required from day one)

- `FileItemByIdDataLoader` — used by `InboxRuleExecutionLog.file`, subscriptions
- `DriveByIdDataLoader` — used by `FileItem.drive`
- `UserByIdDataLoader` — used by `FileVersion.createdBy`, `AuditEntry.performedBy`
- `InboxRuleByIdDataLoader` — used by `InboxRuleExecutionLog.rule`

All DataLoaders inherit `BatchDataLoader<Guid, T>` and are auto-discovered by assembly scanning.

---

## Project Structure

```
src/Strg.GraphQL/
├── IGraphQLMarker.cs               empty assembly anchor interface
├── Topics.cs                       subscription topic name helpers
├── Types/
│   ├── UserType.cs
│   ├── DriveType.cs
│   ├── FileItemType.cs
│   ├── TagType.cs
│   ├── FileVersionType.cs
│   ├── AuditEntryType.cs
│   ├── FileEventType.cs
│   └── Inbox/
│       ├── InboxRuleType.cs
│       ├── InboxRuleExecutionLogType.cs
│       └── InboxFileProcessedEventType.cs
├── Queries/
│   ├── RootQueryExtension.cs       me + namespace fields
│   ├── StorageQueries.cs           marker record
│   ├── DriveQueries.cs             [ExtendObjectType<StorageQueries>]
│   ├── FileQueries.cs              [ExtendObjectType<StorageQueries>]
│   ├── InboxQueries.cs             marker record + [ExtendObjectType<InboxQueries>]
│   └── AdminQueries.cs             marker record + [ExtendObjectType<AdminQueries>]
├── Mutations/
│   ├── RootMutationExtension.cs    namespace fields
│   ├── StorageMutations.cs         marker record
│   ├── DriveMutations.cs           [ExtendObjectType<StorageMutations>]
│   ├── FileMutations.cs            [ExtendObjectType<StorageMutations>]
│   ├── TagMutations.cs             [ExtendObjectType<StorageMutations>]
│   ├── InboxMutations.cs           InboxMutations marker + mutations
│   ├── UserMutations.cs            UserMutations marker + mutations
│   └── AdminMutations.cs           AdminMutations marker + mutations
├── Subscriptions/
│   ├── FileSubscriptions.cs
│   └── InboxSubscriptions.cs
├── DataLoaders/
│   ├── FileItemByIdDataLoader.cs
│   ├── DriveByIdDataLoader.cs
│   ├── UserByIdDataLoader.cs
│   └── InboxRuleByIdDataLoader.cs
├── Errors/
│   └── StrgErrorFilter.cs
├── Inputs/                         C# records for all input types
└── Payloads/                       C# records for all payload types
```
