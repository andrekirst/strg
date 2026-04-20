---
id: STRG-058
title: Configure GraphQL query depth and complexity limits
milestone: v0.1
priority: high
status: open
type: implementation
labels: [graphql, security]
depends_on: [STRG-049]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-058: Configure GraphQL query depth and complexity limits

## Summary

Configure Hot Chocolate's built-in query depth and complexity analysis to prevent deeply nested queries and queries with excessive field selections from causing performance problems or DoS. Disable introspection in production.

## Technical Specification

### Registration in `Program.cs` (extends STRG-049):

```csharp
builder.Services
    .AddGraphQLServer()
    // ... other config ...
    .AddMaxExecutionDepthRule(maxAllowedExecutionDepth: 10)
    .AddQueryComplexityAnalysis(options =>
    {
        options.MaximumAllowed = 1000;
        options.ApplyDefaults = true;
        // Paging costs more (each page request multiplies cost)
        options.DefaultComplexity = 1;
        options.DefaultResolverComplexity = 5;
    });

// Disable introspection in production
if (app.Environment.IsProduction())
{
    builder.Services
        .AddGraphQLServer()
        .ModifyOptions(o => o.EnableSchemaIntrospection = false);
}
```

### Depth limit rationale:

- Max depth of 10 allows `query { drives { files { nodes { tags { key value } } } } }` (depth 5)
- Prevents deeply nested queries like `{ a { b { c { d { e { f { g { h { i { j } } } } } } } } } }`

### Complexity scoring: **developer-assigned** via `[GraphQLComplexity(n)]` attribute on resolvers

```csharp
[GraphQLComplexity(5)]  // explicit — developer assigns cost per resolver
public IQueryable<FileItem> GetFiles(...) { }

[GraphQLComplexity(1)]  // cheap scalar field
public string GetName(FileItem file) => file.Name;
```

Default complexity when attribute is absent: 1. Paged list resolvers must be explicitly annotated with higher complexity.

Typical query cost example:
```
drives (5) → files (5) → nodes (1) → tags (5) → key/value (1 each)
Total for a typical query: ~18 complexity units
```

Maximum of 100 prevents costly combinatorial queries.

### Introspection:

- Development: introspection enabled (for schema explorer tools like Banana Cake Pop)
- Staging/Production: introspection disabled (prevents schema reconnaissance)

### Persisted queries (future — noted here):

In v0.2, implement Automatic Persisted Queries (APQ) to allow only pre-registered query hashes. This fully prevents arbitrary query injection.

## Acceptance Criteria

- [ ] Query with depth > 10 → `400 Bad Request` with `DEPTH_LIMIT_EXCEEDED` error
- [ ] Query with complexity > 100 → `400 Bad Request` with `COMPLEXITY_LIMIT_EXCEEDED` error
- [ ] Valid query with depth 5, complexity 50 → executes normally
- [ ] `__schema` query in development → returns schema
- [ ] `__schema` query in production → `400` (introspection disabled)

## Test Cases

- **TC-001**: 11-level deep query → rejected
- **TC-002**: Normal query → executes
- **TC-003**: `__schema` in development → full schema
- **TC-004**: `__schema` in production environment → error

## Implementation Tasks

- [ ] Add `AddMaxExecutionDepthRule(10)` to Hot Chocolate setup
- [ ] Add `AddQueryComplexityAnalysis(...)` to setup
- [ ] Add production introspection disable via `ModifyOptions`
- [ ] Document complexity scoring in `docs/architecture/`

## Testing Tasks

- [ ] Integration test: deep query rejected at depth 11
- [ ] Integration test: production mode introspection returns error

## Security Review Checklist

- [ ] Introspection disabled in production (schema is not public)
- [ ] Depth limit prevents recursive query attacks
- [ ] Complexity limit prevents "width" attacks (many parallel fields)

## Code Review Checklist

- [ ] Limits configurable via `appsettings.json` (not hardcoded)
- [ ] Error response is a standard GraphQL error (not HTTP 500)

## Definition of Done

- [ ] Depth and complexity limits enforced
- [ ] Introspection disabled in production
