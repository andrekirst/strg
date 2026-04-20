---
id: STRG-049
title: Configure Hot Chocolate GraphQL server
milestone: v0.1
priority: critical
status: done
type: implementation
labels: [graphql, api]
depends_on: [STRG-013]
blocks: [STRG-050, STRG-051, STRG-052, STRG-053, STRG-057, STRG-058]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-049: Configure Hot Chocolate GraphQL server

## Summary

Configure Hot Chocolate GraphQL server with EF Core integration, Relay cursor pagination, authorization, and WebSocket/SSE subscription transport. This is the foundation all GraphQL types and resolvers build on.

## Technical Specification

### Package version: Hot Chocolate **15.x** (latest stable — not 16.x preview)

### Packages: `HotChocolate.AspNetCore`, `HotChocolate.Data.EntityFramework`, `HotChocolate.AspNetCore.Authorization`, `StackExchange.Redis`

### Schema style: **Code-first** — separate `ObjectType<T>` descriptor classes per domain type. Domain entities have zero GraphQL attributes. Sensitive fields (`Drive.ProviderConfig`, `TenantId`) excluded explicitly in descriptors.

### Assembly marker (`src/Strg.GraphQL/IGraphQLMarker.cs`):

```csharp
// Empty interface — used as assembly anchor for AddTypes() discovery
internal interface IGraphQLMarker { }
```

### Registration in `Program.cs` (OCP — never needs editing when adding new types):

```csharp
builder.Services
    .AddGraphQLServer()
    .AddQueryType(q => q.Name("Query"))
    .AddMutationType(m => m.Name("Mutation"))
    .AddSubscriptionType(s => s.Name("Subscription"))
    .AddTypes(typeof(IGraphQLMarker).Assembly)  // discovers all types/extensions automatically
    .AddGlobalObjectIdentification()            // Relay Node interface + node(id) query
    .AddFiltering()
    .AddSorting()
    .AddAuthorization()
    .RegisterDbContext<StrgDbContext>(DbContextKind.Pooled)
    .AddErrorFilter<StrgErrorFilter>()          // registered before types (order matters)
    .AddMaxExecutionDepthRule(10)
    .ModifyRequestOptions(o => o.Complexity.MaximumAllowed = 100);

// Subscription backplane: in-memory for dev/tests, Redis for production
if (builder.Environment.IsDevelopment())
    builder.Services.AddGraphQLServer().AddInMemorySubscriptions();
else
    builder.Services.AddGraphQLServer()
        .AddRedisSubscriptions(sp =>
            ConnectionMultiplexer.Connect(
                sp.GetRequiredService<IConfiguration>()["Redis:ConnectionString"]!));

// Disable introspection in production
if (!builder.Environment.IsDevelopment())
    builder.Services.AddGraphQLServer()
        .ModifyOptions(o => o.EnableSchemaIntrospection = false);

app.UseWebSockets();
app.MapGraphQL("/graphql");
```

### Namespace resolver pattern:

Root namespace fields return marker objects; sub-fields live on extension types auto-discovered by `AddTypes()`:

```csharp
[ExtendObjectType("Query")]
public sealed class RootQueryExtension
{
    public StorageQueries Storage() => new();
    public InboxQueries Inbox() => new();

    [Authorize(Policy = "Admin")]
    public AdminQueries Admin() => new();

    [Authorize]
    public async Task<User> Me(
        [Service] StrgDbContext db,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
        => await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
           ?? throw new UnauthorizedAccessException();
}

public sealed record StorageQueries;
public sealed record InboxQueries;
public sealed record AdminQueries;

[ExtendObjectType<StorageQueries>]
public sealed class DriveQueries { ... }  // auto-discovered

[ExtendObjectType<StorageMutations>]
public sealed class DriveMutations { ... }  // auto-discovered
```

### Schema endpoints:

- `POST /graphql` — queries and mutations
- `WS ws://host/graphql` — WebSocket subscriptions (graphql-ws protocol)
- `GET /graphql?transport=sse` — Server-Sent Events subscriptions

## Acceptance Criteria

- [ ] `POST /graphql` with valid query returns JSON response
- [ ] `POST /graphql` without auth token → `errors` with `UNAUTHENTICATED` code
- [ ] GraphQL introspection works in development; disabled in production
- [ ] WebSocket subscriptions work at `ws://host/graphql`
- [ ] SSE subscriptions work at `GET /graphql?transport=sse`
- [ ] Schema uses code-first `ObjectType<T>` descriptors (no annotation attributes on domain entities)
- [ ] DataLoaders auto-discovered by assembly scanning — no manual registration needed
- [ ] `query { storage { drives { nodes { id name } } } }` — namespaced query works
- [ ] `mutation { storage { createDrive(...) { drive { id } errors { code } } } }` — payload pattern works
- [ ] `StoragePathException` → GraphQL error with `code: "INVALID_PATH"`
- [ ] Unhandled server errors → generic `INTERNAL_ERROR` (no stack trace in production)

## Test Cases

- **TC-001**: Query `{ __typename }` → returns `"Query"`
- **TC-002**: Query without auth → errors with `UNAUTHENTICATED`
- **TC-003**: Query with auth → 200 with data
- **TC-004**: WebSocket connection → subscription message received
- **TC-005**: Introspection in development → succeeds; in production → 400
- **TC-006**: Query `{ me { email } }` → returns current user

## Implementation Tasks

- [ ] Install Hot Chocolate 15.x packages + StackExchange.Redis
- [ ] Create `IGraphQLMarker.cs` in `src/Strg.GraphQL/`
- [ ] Configure `AddGraphQLServer()` in `Program.cs` with assembly scanning
- [ ] Create `RootQueryExtension.cs` with namespace fields and `me`
- [ ] Create `RootMutationExtension.cs` with namespace fields
- [ ] Create `StrgErrorFilter.cs` (STRG-056)
- [ ] Create `Strg.GraphQL/` project structure (Types/, Queries/, Mutations/, Subscriptions/, DataLoaders/, Errors/, Inputs/, Payloads/)
- [ ] Enable WebSockets in middleware pipeline (before `MapGraphQL`)
- [ ] Disable introspection in production

## Security Review Checklist

- [ ] Introspection disabled in production (reveals schema to attackers)
- [ ] Query depth limit set to 10 (prevent deeply nested query attacks)
- [ ] Query complexity limit set to 100
- [ ] All mutations require authentication
- [ ] Error messages in production don't expose stack traces

## Code Review Checklist

- [ ] `AddInMemorySubscriptions()` used only in development/tests; Redis in production
- [ ] Error filter registered before types (order matters in HC)
- [ ] DbContext registered with `DbContextKind.Pooled` for performance
- [ ] `IGraphQLMarker` is `internal` — not a public API
- [ ] No manual `.AddTypeExtension<T>()` calls — assembly scanning handles all discovery

## Definition of Done

- [ ] Basic GraphQL query works
- [ ] Auth enforcement verified
- [ ] WebSocket subscription tested
- [ ] Namespace queries and mutations work end-to-end
