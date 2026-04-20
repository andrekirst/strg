---
id: STRG-058
title: Configure GraphQL query depth and complexity limits
milestone: v0.1
priority: high
status: done
type: implementation
labels: [graphql, security]
depends_on: [STRG-049]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-058: Configure GraphQL query depth and complexity limits

## Summary

Configure Hot Chocolate's built-in depth and complexity analysis to prevent DoS attacks. All limits are configured in STRG-049's server registration via `AddMaxExecutionDepthRule` and `ModifyRequestOptions`. Introspection is disabled in production. No additional type registrations are needed — the assembly scanning approach in STRG-049 handles everything.

## Technical Specification

### Limits (configured in STRG-049 server setup):

```csharp
builder.Services
    .AddGraphQLServer()
    // depth: max 10 levels of nesting
    .AddMaxExecutionDepthRule(maxAllowedExecutionDepth: 10)
    // complexity: max 100 units per query
    .ModifyRequestOptions(o => o.Complexity.MaximumAllowed = 100);

// Disable introspection in production
if (!builder.Environment.IsDevelopment())
    builder.Services.AddGraphQLServer()
        .ModifyOptions(o => o.EnableSchemaIntrospection = false);
```

### Depth limit rationale (max 10):

Allows normal queries like:
```
query { storage { drives { nodes { files { nodes { tags { nodes { key value } } } } } } } }
#                  1        2      3      4      5        6      7    8 = depth 8 — allowed
```

Prevents:
```
{ a { b { c { d { e { f { g { h { i { j { k } } } } } } } } } } }
# depth 11 — rejected
```

### Complexity scoring (developer-assigned, max 100):

```csharp
// Paged list resolvers must be annotated
[GraphQLComplexity(5)]
[UsePaging]
public IQueryable<Drive> GetDrives(...) { }

[GraphQLComplexity(5)]
[UsePaging]
public IQueryable<FileItem> GetFiles(...) { }

// Scalar fields default to complexity 1 (no annotation needed)
public string GetName(FileItem file) => file.Name;
```

Typical query cost example:
```
storage(1) → drives(5) → nodes(1) → files(5) → nodes(1) → tags(5) → nodes(1) → key(1) value(1)
Total: ~21 complexity units — well within 100
```

### Introspection:

- **Development**: enabled — Banana Cake Pop and other tools need it
- **Production**: disabled — prevents schema reconnaissance attacks

### Future (v0.2): Automatic Persisted Queries (APQ)

Register allowed query hashes to fully prevent arbitrary query injection. Noted here as the intended next step after complexity/depth limits.

## Acceptance Criteria

- [ ] Query with depth > 10 → rejected with error code `HC0062` or similar
- [ ] Query with complexity > 100 → rejected
- [ ] Valid query with depth 8, complexity ~50 → executes normally
- [ ] `__schema` in development → returns schema
- [ ] `__schema` in production → rejected (introspection disabled)
- [ ] Paged list resolvers annotated with `[GraphQLComplexity(5)]`

## Test Cases

- **TC-001**: 11-level deep query → rejected
- **TC-002**: Normal query (depth 5) → executes
- **TC-003**: `__schema` in development → full schema
- **TC-004**: `__schema` in production environment → error
- **TC-005**: Query combining 20+ paged fields → exceeds complexity 100 → rejected

## Implementation Tasks

- [ ] Add `AddMaxExecutionDepthRule(10)` to HC setup in `Program.cs` (STRG-049)
- [ ] Add `.ModifyRequestOptions(o => o.Complexity.MaximumAllowed = 100)` to HC setup
- [ ] Disable introspection in production via `ModifyOptions`
- [ ] Annotate all paged list resolvers with `[GraphQLComplexity(5)]`
- [ ] No type registration needed — limits are server-level config only

## Security Review Checklist

- [ ] Introspection disabled in production (schema not public)
- [ ] Depth limit prevents recursive query attacks
- [ ] Complexity limit prevents combinatorial "width" attacks

## Code Review Checklist

- [ ] Limits are in `Program.cs` HC setup (not hardcoded per-resolver)
- [ ] All `[UsePaging]` resolvers have `[GraphQLComplexity(5)]`
- [ ] Error response is a standard GraphQL error (not HTTP 500)

## Definition of Done

- [ ] Depth and complexity limits enforced in integration tests
- [ ] Introspection disabled in production
- [ ] All paged resolvers annotated with complexity
