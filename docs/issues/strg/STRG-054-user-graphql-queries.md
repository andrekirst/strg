---
id: STRG-054
title: Implement user profile GraphQL queries and mutations
milestone: v0.1
priority: medium
status: done
type: implementation
labels: [graphql, auth, users]
depends_on: [STRG-049, STRG-011, STRG-014]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-054: Implement user profile GraphQL queries and mutations

## Summary

Implement the `me` query (current user's profile, at root — not namespaced) and `updateProfile` / `changePassword` mutations under the `user` namespace. Admin-only user management mutations (`updateUserQuota`, `lockUser`, `unlockUser`) live under the `admin` namespace. All mutations return Relay-style payload types.

## Technical Specification

### Schema:

```graphql
type Query {
  me: User!    # at root — always available to any authenticated user
}

type User implements Node {
  id: ID!
  email: String!
  displayName: String!
  quotaBytes: Long!
  usedBytes: Long!
  createdAt: DateTime!
  updatedAt: DateTime!
}

# Under mutation { user { ... } }
type UserMutations {
  updateProfile(input: UpdateProfileInput!): UpdateProfilePayload!
  changePassword(input: ChangePasswordInput!): ChangePasswordPayload!
}

type UpdateProfilePayload  { user: User  errors: [UserError!] }
type ChangePasswordPayload { user: User  errors: [UserError!] }

input UpdateProfileInput  { displayName: String  email: String }
input ChangePasswordInput { currentPassword: String!  newPassword: String! }

# Under mutation { admin { ... } }
type AdminMutations {
  updateUserQuota(input: UpdateUserQuotaInput!): UpdateUserQuotaPayload!
  lockUser(input: LockUserInput!): LockUserPayload!
  unlockUser(input: UnlockUserInput!): UnlockUserPayload!
}

type UpdateUserQuotaPayload { user: User  errors: [UserError!] }
type LockUserPayload        { user: User  errors: [UserError!] }
type UnlockUserPayload      { user: User  errors: [UserError!] }

input UpdateUserQuotaInput { userId: ID!  quotaBytes: Long! }
input LockUserInput        { userId: ID!  reason: String }
input UnlockUserInput      { userId: ID! }

# Under query { admin { ... } }
type AdminQueries {
  users(first: Int, after: String): UserConnection!
  user(id: ID!): User
}
```

### File: `src/Strg.GraphQL/Queries/RootQueryExtension.cs` (me lives here)

```csharp
[ExtendObjectType("Query")]
public sealed class RootQueryExtension
{
    [Authorize]
    public async Task<User> Me(
        [Service] StrgDbContext db,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
        => await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
           ?? throw new UnauthorizedAccessException();

    public StorageQueries Storage() => new();
    public InboxQueries Inbox() => new();

    [Authorize(Policy = "Admin")]
    public AdminQueries Admin() => new();
}
```

### File: `src/Strg.GraphQL/Mutations/UserMutations.cs`

```csharp
[ExtendObjectType<UserMutations>]  // extends the UserMutations marker record
public sealed class UserMutationHandlers
{
    [Authorize]
    public async Task<UpdateProfilePayload> UpdateProfileAsync(
        UpdateProfileInput input,
        [Service] StrgDbContext db,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        if (input.DisplayName?.Length > 255)
            return new UpdateProfilePayload(null, [new UserError("VALIDATION_ERROR",
                "displayName too long.", "displayName")]);

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        if (input.DisplayName is not null) user.DisplayName = input.DisplayName;
        if (input.Email is not null) user.Email = input.Email;
        await db.SaveChangesAsync(ct);
        return new UpdateProfilePayload(user, null);
    }
}
```

### File: `src/Strg.GraphQL/Mutations/AdminMutations.cs`

```csharp
[ExtendObjectType<AdminMutations>]
public sealed class AdminMutationHandlers
{
    [Authorize(Policy = "Admin")]
    public async Task<UpdateUserQuotaPayload> UpdateUserQuotaAsync(
        UpdateUserQuotaInput input,
        [Service] StrgDbContext db,
        CancellationToken ct)
    {
        if (input.QuotaBytes < 0)
            return new UpdateUserQuotaPayload(null, [new UserError("VALIDATION_ERROR",
                "Quota must be non-negative.", "quotaBytes")]);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == (Guid)input.UserId, ct);
        if (user is null)
            return new UpdateUserQuotaPayload(null, [new UserError("NOT_FOUND", "User not found.", null)]);

        user.QuotaBytes = input.QuotaBytes;
        await db.SaveChangesAsync(ct);
        return new UpdateUserQuotaPayload(user, null);
    }
}
```

## Acceptance Criteria

- [ ] `query { me { email usedBytes } }` → correct values from JWT + DB
- [ ] `mutation { user { updateProfile(input: { displayName: "Alice" }) { user { displayName } errors { code } } } }` → updated
- [ ] `displayName` > 255 chars → `errors: [{ code: "VALIDATION_ERROR", field: "displayName" }]`
- [ ] `mutation { admin { updateUserQuota(input: { userId: "...", quotaBytes: 5368709120 }) { user { quotaBytes } errors { code } } } }` → updated (Admin only)
- [ ] `lockUser` sets `User.LockedUntil` to far future
- [ ] `unlockUser` clears `LockedUntil`
- [ ] Non-admin calling admin mutations → `UNAUTHORIZED`

## Test Cases

- **TC-001**: `me` query returns authenticated user's data
- **TC-002**: `updateProfile` → `me` query returns new display name
- **TC-003**: Non-admin `updateUserQuota` → `UNAUTHORIZED`
- **TC-004**: `me` with invalid JWT → `UNAUTHENTICATED`
- **TC-005**: `changePassword` with wrong `currentPassword` → `errors: [{ code: "INVALID_PASSWORD" }]`

## Implementation Tasks

- [ ] `me` field in `RootQueryExtension.cs` (STRG-049)
- [ ] Create `UserType.cs` in `src/Strg.GraphQL/Types/` (implements Node, ignores TenantId)
- [ ] Create `UserMutations.cs` with `[ExtendObjectType<UserMutations>]`
- [ ] Create `AdminMutations.cs` with `[ExtendObjectType<AdminMutations>]`
- [ ] Create payload and input records in `src/Strg.GraphQL/Payloads/` and `Inputs/`
- [ ] Types are auto-discovered by `AddTypes()` — no manual registration

## Security Review Checklist

- [ ] `me` cannot return another user's data (userId from JWT only)
- [ ] `updateUserQuota` requires Admin (users cannot inflate their own quota)
- [ ] `quotaBytes < 0` rejected
- [ ] `TenantId` not exposed in `UserType`

## Definition of Done

- [ ] `me` query and `updateProfile` mutation working with payload pattern
- [ ] Admin mutations protected and returning typed payloads
