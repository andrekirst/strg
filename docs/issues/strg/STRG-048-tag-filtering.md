---
id: STRG-048
title: Implement tag-based file filtering in GraphQL and REST queries
milestone: v0.1
priority: medium
status: open
type: implementation
labels: [graphql, tags, api]
depends_on: [STRG-046, STRG-050]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-048: Implement tag-based file filtering in GraphQL and REST queries

## Summary

Enable filtering files by tag key/value in both GraphQL (via Hot Chocolate's `[UseFiltering]`) and the REST listing endpoint. Tags are user-scoped, so only the requesting user's tags are visible.

## Technical Specification

### GraphQL filter (automatic via `[UseFiltering]` on `FileQueries.GetFiles`):

Hot Chocolate auto-generates filter input from the `FileItem` type. Since `FileItem.Tags` is a navigation property (`ICollection<Tag>`), the generated filter supports:

```graphql
query {
  files(driveId: "...", where: {
    tags: {
      some: {
        key: { eq: "project" }
        value: { eq: "acme" }
      }
    }
  }) {
    nodes { id name }
  }
}
```

### User isolation for tag queries:

The `FileItem.Tags` navigation should be scoped to the current user. This requires a global query filter on `Tag` in `StrgDbContext`:

```csharp
// In OnModelCreating:
modelBuilder.Entity<Tag>().HasQueryFilter(t =>
    t.TenantId == currentTenantId &&
    t.UserId == currentUserId);  // tags visible only to owner
```

Where `currentUserId` is resolved from `ITenantContext.UserId`.

### REST tag filter (in `FileEndpoints.cs`):

```
GET /api/v1/drives/{driveId}/files?tagKey=project&tagValue=acme
```

```csharp
if (request.TagKey is not null)
{
    query = query.Where(f => f.Tags.Any(t =>
        t.UserId == userId &&
        t.Key == request.TagKey.ToLower() &&
        (request.TagValue == null || t.Value == request.TagValue)));
}
```

## Acceptance Criteria

- [ ] GraphQL `where: { tags: { some: { key: { eq: "project" } } } }` → filtered results
- [ ] Tag filter returns only files tagged by the requesting user (not other users' tags)
- [ ] REST `?tagKey=project&tagValue=acme` → filtered file list
- [ ] `?tagKey=project` (no value) → any file with that key, regardless of value
- [ ] `FileItem.tags` in GraphQL returns only the requesting user's tags

## Test Cases

- **TC-001**: User A tags file with `project=acme`; User B queries → file NOT in results for User B
- **TC-002**: `where: { tags: { some: { key: "project" } } }` → files with any `project` tag value
- **TC-003**: REST `?tagKey=X` → correctly filtered

## Implementation Tasks

- [ ] Add `UserId` to `ITenantContext` (in addition to `TenantId`)
- [ ] Add `Tag` global query filter in `StrgDbContext` (user-scoped)
- [ ] Add `tagKey`/`tagValue` parameters to REST file listing handler
- [ ] Verify Hot Chocolate generates correct SQL for tag filter

## Testing Tasks

- [ ] Integration test: user isolation for tag filters
- [ ] Integration test: `?tagKey` REST filter works

## Security Review Checklist

- [ ] `FileItem.tags` in GraphQL response contains ONLY the requesting user's tags
- [ ] Tag global query filter applies to all tag queries (not just explicit filter)

## Code Review Checklist

- [ ] Tag key compared case-insensitively (`ToLower()` or EF Core `EF.Functions.Like`)
- [ ] `UserId` filter applied server-side (EF Core WHERE clause, not client-side filter)

## Definition of Done

- [ ] Tag filtering works in both GraphQL and REST
- [ ] User isolation verified in test
