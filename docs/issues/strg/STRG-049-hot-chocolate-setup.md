---
id: STRG-049
title: Configure Hot Chocolate GraphQL server
milestone: v0.1
priority: critical
status: open
type: implementation
labels: [graphql, api]
depends_on: [STRG-013]
blocks: [STRG-050, STRG-051, STRG-052, STRG-053, STRG-057, STRG-058]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-049: Configure Hot Chocolate GraphQL server

## Summary

Configure Hot Chocolate GraphQL server with EF Core integration, Relay cursor pagination, authorization, and the WebSocket transport for subscriptions. This is the foundation all GraphQL types and resolvers build on.

## Technical Specification

### Package version: Hot Chocolate **15.x** (latest stable — not 16.x preview)

### Packages: `HotChocolate.AspNetCore`, `HotChocolate.Data.EntityFramework`, `HotChocolate.AspNetCore.Authorization`

### Schema style: **Code-first** — separate `ObjectType<T>` descriptor classes per domain type. Domain entities have zero GraphQL attributes. Sensitive fields (e.g. `Drive.ProviderConfig`) excluded explicitly in descriptors.

### Registration in `Program.cs`:

```csharp
builder.Services
    .AddGraphQLServer()
    .AddQueryType(q => q.Name("Query"))
    .AddMutationType(m => m.Name("Mutation"))
    .AddSubscriptionType(s => s.Name("Subscription"))
    .AddFiltering()
    .AddSorting()
    .AddProjections()
    .AddAuthorization()
    .AddInMemorySubscriptions()  // v0.1: in-memory; v0.2+: RabbitMQ backplane
    .RegisterDbContext<StrgDbContext>(DbContextKind.Pooled)
    .AddErrorFilter<StrgErrorFilter>()
    .AddDataLoader<FileItemByIdDataLoader>(); // DataLoader wired from day one — prevents N+1

// Subscriptions: both WebSockets and SSE
app.UseWebSockets();
app.MapGraphQL("/graphql");
```

### `StrgErrorFilter`: maps domain exceptions to GraphQL errors with `extensions.code`.

### Authorization integration:

```csharp
// Require auth for all GraphQL operations by default
.AddDefaultFieldMiddleware()
.AddTypeExtension<QueryTypeExtensions>()
```

## Acceptance Criteria

- [ ] `POST /graphql` with valid query returns JSON response
- [ ] `POST /graphql` without auth token → `errors` with `UNAUTHENTICATED` code
- [ ] GraphQL introspection works (for development; disabled in production)
- [ ] WebSocket subscriptions work at `ws://host/graphql`
- [ ] SSE subscriptions work at `GET /graphql?transport=sse`
- [ ] Schema uses code-first `ObjectType<T>` descriptors (no annotation attributes on domain entities)
- [ ] DataLoader wired from day one — `IFileItemByIdDataLoader` batches DB calls correctly
- [ ] `[UseProjection]` automatically generates SELECT clauses from requested fields
- [ ] `[UseFiltering]` generates WHERE clauses from filter arguments
- [ ] `[UsePaging]` generates Relay cursor pagination from connection arguments
- [ ] `StoragePathException` → GraphQL error with `code: "INVALID_PATH"`
- [ ] Unhandled server errors → generic `INTERNAL_ERROR` (no stack trace in production)

## Test Cases

- **TC-001**: Query `{ __typename }` → returns `"Query"`
- **TC-002**: Query without auth → errors with `UNAUTHENTICATED`
- **TC-003**: Query with auth → 200 with data
- **TC-004**: WebSocket connection → subscription message received
- **TC-005**: Introspection in development → succeeds; in production → 400

## Implementation Tasks

- [ ] Install Hot Chocolate packages
- [ ] Configure `AddGraphQLServer()` in `Program.cs`
- [ ] Create `StrgErrorFilter.cs`
- [ ] Create `Strg.GraphQL/` project structure (Types/, Queries/, Mutations/, Subscriptions/)
- [ ] Enable WebSockets in middleware pipeline (before `MapGraphQL`)
- [ ] Disable introspection in production

## Security Review Checklist

- [ ] Introspection disabled in production (reveals schema to attackers)
- [ ] Query depth limit set (prevent deeply nested query attacks)
- [ ] Query complexity limit set
- [ ] All mutations require authentication
- [ ] Error messages in production don't expose stack traces

## Code Review Checklist

- [ ] `AddInMemorySubscriptions()` noted as dev-only (swap to Redis in v0.3)
- [ ] Error filter is registered before types
- [ ] DbContext registered with `DbContextKind.Pooled` for performance

## Definition of Done

- [ ] Basic GraphQL query works
- [ ] Auth enforcement verified
- [ ] WebSocket subscription tested
