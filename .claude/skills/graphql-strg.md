---
name: graphql-strg
description: Use when implementing, reviewing, or extending any GraphQL type, query, mutation, subscription, or DataLoader in the strg project. Enforces the agreed schema design from docs/superpowers/specs/2026-04-20-graphql-interface-design.md.
---

# GraphQL Implementation Guide for strg

This skill governs all GraphQL work in `src/Strg.GraphQl/`. Deviating from these patterns requires a design revision — do not improvise.

## Non-Negotiable Rules

### 1. Assembly scanning — never manual registration

```csharp
// CORRECT — central registration never changes
.AddTypes(typeof(IGraphQLMarker).Assembly)

// WRONG — do not add these
.AddTypeExtension<MyNewType>()
.AddType<SomeType>()
```

### 2. Namespace pattern — never extend root directly

```csharp
// CORRECT — extend the namespace marker record
[ExtendObjectType<StorageQueries>]
public sealed class FileQueries { ... }

[ExtendObjectType<StorageMutations>]
public sealed class FileMutations { ... }

// WRONG — never extend root Query/Mutation directly for domain types
[ExtendObjectType("Query")]
public sealed class FileQueries { ... }

[ExtendObjectType("Mutation")]
public sealed class FileMutations { ... }
```

Exception: `RootQueryExtension` and `RootMutationExtension` extend root — these are the namespace wiring files only.

### 3. Relay payload for every mutation

```csharp
// CORRECT
public sealed Task<CreateFolderPayload> CreateFolderAsync(...)
// payload: { file: FileItem  errors: [UserError!] }

// WRONG — never return entity or bool directly
public sealed Task<FileItem> CreateFolderAsync(...)
public sealed Task<bool> DeleteFileAsync(...)
```

### 4. Relay cursor pagination everywhere

```csharp
// CORRECT
[UsePaging(DefaultPageSize = 50, MaxPageSize = 200)]
public IQueryable<FileItem> GetFiles(...) { }

// WRONG — never return plain lists
public Task<List<FileItem>> GetFiles(...) { }
public Task<FileItem[]> GetFiles(...) { }
```

### 5. Complexity annotation on every paged resolver

```csharp
// CORRECT
[UsePaging(DefaultPageSize = 50, MaxPageSize = 200)]
[GraphQLComplexity(5)]
public IQueryable<Drive> GetDrives(...) { }

// WRONG — missing annotation, default complexity of 1 will be too low
[UsePaging]
public IQueryable<Drive> GetDrives(...) { }
```

### 6. Handcrafted filter inputs — no HC auto-generated filtering

```csharp
// CORRECT — accept FileFilterInput and apply manually
public IQueryable<FileItem> GetFiles(FileFilterInput? filter, ...) { ... }

// WRONG — do not use HC's [UseFiltering] attribute
[UseFiltering]
public IQueryable<FileItem> GetFiles(...) { }
```

### 7. Node interface on all entity types

```csharp
// CORRECT
public sealed class FileItemType : ObjectType<FileItem>
{
    protected override void Configure(IObjectTypeDescriptor<FileItem> descriptor)
    {
        descriptor.ImplementsNode().IdField(f => f.Id);
        // ...
    }
}

// WRONG — missing Node implementation
public sealed class FileItemType : ObjectType<FileItem> { ... }  // no ImplementsNode()
```

### 8. DataLoader for every related entity load

```csharp
// CORRECT — batch load via DataLoader
var file = await fileLoader.LoadAsync(fileEvent.FileId, ct);

// WRONG — individual DB query (causes N+1)
var file = await db.Files.FirstOrDefaultAsync(f => f.Id == fileEvent.FileId, ct);
```

### 9. Fields never to expose

| Field | Type | Why |
|---|---|---|
| `ProviderConfig` | Drive | Storage credentials |
| `TenantId` | All entities | Internal isolation key |
| Stack traces | Errors | Security — use `StrgErrorFilter` |

```csharp
// Required in every entity type descriptor
descriptor.Field(d => d.ProviderConfig).Ignore();
descriptor.Field(d => d.TenantId).Ignore();
```

### 10. User identity from JWT only

```csharp
// CORRECT
public Task<...> DoSomethingAsync(
    SomeInput input,
    [GlobalState("userId")] Guid userId,    // from JWT sub claim
    [GlobalState("tenantId")] Guid tenantId) // from JWT tenant_id claim

// WRONG — never trust client-supplied IDs
public Task<...> DoSomethingAsync(Guid userId, ...) // client can pass any userId
```

---

## Namespace Structure

```
Query root
├── me: User!                        ← RootQueryExtension (root level)
├── storage: StorageQueries!         ← StorageQueries marker record
│   ├── drives(...)                  ← [ExtendObjectType<StorageQueries>] DriveQueries
│   ├── drive(id)                    ← [ExtendObjectType<StorageQueries>] DriveQueries
│   ├── files(driveId, ...)          ← [ExtendObjectType<StorageQueries>] FileQueries
│   └── file(id)                     ← [ExtendObjectType<StorageQueries>] FileQueries
├── inbox: InboxQueries!             ← InboxQueries marker record
│   ├── rules(...)                   ← [ExtendObjectType<InboxQueries>]
│   ├── rule(id)                     ← [ExtendObjectType<InboxQueries>]
│   ├── files(...)                   ← [ExtendObjectType<InboxQueries>]
│   └── executionLogs(...)           ← [ExtendObjectType<InboxQueries>]
└── admin: AdminQueries!             ← AdminQueries marker record [Authorize(Policy="Admin")]
    ├── auditLog(...)                ← [ExtendObjectType<AdminQueries>]
    ├── users(...)                   ← [ExtendObjectType<AdminQueries>]
    └── user(id)                     ← [ExtendObjectType<AdminQueries>]

Mutation root
├── storage: StorageMutations!       ← drives, files, tags under StorageMutations
├── inbox: InboxMutations!           ← inbox rules under InboxMutations
├── user: UserMutations!             ← profile, password under UserMutations
└── admin: AdminMutations!           ← quota, lock/unlock under AdminMutations

Subscription root
├── fileEvents(driveId: ID!)         ← FileSubscriptions
└── inboxFileProcessed               ← InboxSubscriptions
```

---

## Subscription Backplane

```csharp
// Development / tests — in-memory
if (env.IsDevelopment())
    .AddInMemorySubscriptions();

// Production — Redis
else
    .AddRedisSubscriptions(sp =>
        ConnectionMultiplexer.Connect(config["Redis:ConnectionString"]!));
```

- Never use in-memory subscriptions in production (breaks multi-instance)
- Topics defined in `Topics.cs` — never use raw strings in publisher or subscriber
- Tenant isolation guard in every subscription resolver

---

## Error Handling

Two surfaces — know both:

```csharp
// 1. StrgErrorFilter — safety net for unhandled exceptions escaping resolvers
throw new StoragePathException("...");  // filter catches, maps to INVALID_PATH code

// 2. Payload errors — preferred for predictable business errors
return new CreateFolderPayload(null, [new UserError("INVALID_PATH", ex.Message, "path")]);
```

`UserError.field` must be the exact input field name (e.g., `"name"`, `"path"`) for form-field highlighting.

---

## File Structure Checklist (when adding a new domain area)

- [ ] Descriptor type in `Types/` — implements `Node`, ignores sensitive fields
- [ ] Query class in `Queries/` with `[ExtendObjectType<NamespaceQueries>]`
- [ ] Mutation class in `Mutations/` with `[ExtendObjectType<NamespaceMutations>]`
- [ ] Payload records in `Payloads/` with `errors: [UserError!]`
- [ ] Input records in `Inputs/`
- [ ] DataLoader in `DataLoaders/` if resolving related entities
- [ ] No manual registration in `Program.cs` — assembly scanning handles it
