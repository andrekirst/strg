---
id: STRG-054
title: Implement user profile GraphQL queries and mutations
milestone: v0.1
priority: medium
status: open
type: implementation
labels: [graphql, auth, users]
depends_on: [STRG-049, STRG-011, STRG-014]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-054: Implement user profile GraphQL queries and mutations

## Summary

Implement `me` query (returns current user's profile) and `updateProfile` mutation (display name). Quota information is included in the `me` response. Admin-only `listUsers` and `updateUserQuota` mutations are also covered here.

## Technical Specification

### Schema:

```graphql
type Query {
  me: UserProfile!
}

type Mutation {
  updateProfile(displayName: String!): UserProfile!
  # Admin only:
  listUsers(first: Int, after: String): UserProfileConnection!
  updateUserQuota(userId: UUID!, quotaBytes: Long!): UserProfile!
  lockUser(userId: UUID!): Boolean!
  unlockUser(userId: UUID!): Boolean!
}

type UserProfile {
  id: UUID!
  email: String!
  displayName: String!
  role: UserRole!
  quotaBytes: Long!
  usedBytes: Long!
  freeBytes: Long!
  usagePercent: Float!
  createdAt: DateTime!
}
```

### File: `src/Strg.GraphQL/Queries/UserQueries.cs`

```csharp
[ExtendObjectType("Query")]
public class UserQueries
{
    [Authorize]
    public async Task<User> Me(
        [Service] StrgDbContext db,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        return await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedAccessException();
    }
}
```

### File: `src/Strg.GraphQL/Mutations/UserMutations.cs`

```csharp
[ExtendObjectType("Mutation")]
public class UserMutations
{
    [Authorize]
    public async Task<User> UpdateProfile(
        string displayName,
        [Service] StrgDbContext db,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        if (displayName.Length > 255)
            throw new ValidationException("displayName too long.");

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        user.DisplayName = displayName;
        await db.SaveChangesAsync(ct);
        return user;
    }

    [Authorize(Policy = "Admin")]
    public async Task<User> UpdateUserQuota(
        Guid userId, long quotaBytes,
        [Service] StrgDbContext db,
        CancellationToken ct)
    {
        if (quotaBytes < 0) throw new ValidationException("Quota must be non-negative.");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException();
        user.QuotaBytes = quotaBytes;
        await db.SaveChangesAsync(ct);
        return user;
    }
}
```

## Acceptance Criteria

- [ ] `query { me { email usedBytes freeBytes usagePercent } }` → correct values
- [ ] `mutation { updateProfile(displayName: "Alice") { displayName } }` → updated
- [ ] `displayName` > 255 chars → validation error
- [ ] `updateUserQuota` requires Admin role
- [ ] `lockUser` sets `User.LockedUntil` to 100 years from now
- [ ] `unlockUser` clears `LockedUntil`

## Test Cases

- **TC-001**: `me` query returns authenticated user's data
- **TC-002**: `updateProfile` → `me` query returns new display name
- **TC-003**: Non-admin `updateUserQuota` → `UNAUTHORIZED`
- **TC-004**: `me` with invalid JWT → `UNAUTHENTICATED`

## Implementation Tasks

- [ ] Create `UserQueries.cs`
- [ ] Create `UserMutations.cs`
- [ ] Create `UserType.cs` (expose `freeBytes`, `usagePercent` as computed fields)
- [ ] Register types in Hot Chocolate setup

## Testing Tasks

- [ ] Integration test: `me` query returns correct user
- [ ] Integration test: `updateProfile` persists to DB

## Security Review Checklist

- [ ] `me` query cannot return other users' data
- [ ] `updateUserQuota` requires Admin (non-admin cannot inflate their own quota)
- [ ] `quotaBytes < 0` rejected (prevents negative quota bypass)

## Definition of Done

- [ ] `me` query and `updateProfile` mutation working
- [ ] Admin mutations protected
