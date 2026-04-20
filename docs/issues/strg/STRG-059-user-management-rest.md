---
id: STRG-059
title: Implement user management REST endpoints (GET /users/me, admin CRUD)
milestone: v0.1
priority: medium
status: open
type: implementation
labels: [api, auth, users, rest]
depends_on: [STRG-011, STRG-013, STRG-014]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-059: Implement user management REST endpoints

## Summary

Implement REST endpoints for user profile and admin user management. Complements the GraphQL user mutations (STRG-054). The REST endpoints are the primary interface for tooling/automation; GraphQL is for interactive clients.

## Technical Specification

### Routes:

```
GET    /api/v1/users/me                          → current user's profile
PUT    /api/v1/users/me                          → update display name
GET    /api/v1/users          [Admin]             → paginated user list
GET    /api/v1/users/{userId} [Admin]             → single user
PUT    /api/v1/users/{userId}/quota [Admin]       → update quota
POST   /api/v1/users/{userId}/lock [Admin]        → lock account
DELETE /api/v1/users/{userId}/lock [Admin]        → unlock account
```

### Responses:

```json
{
  "id": "uuid",
  "email": "user@example.com",
  "displayName": "Alice",
  "role": "User",
  "quotaBytes": 10737418240,
  "usedBytes": 1073741824,
  "freeBytes": 9663676416,
  "usagePercent": 10.0,
  "isLocked": false,
  "createdAt": "2024-01-01T00:00:00Z"
}
```

### UserDto (never exposes PasswordHash):

```csharp
public record UserDto(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    long QuotaBytes,
    long UsedBytes,
    long FreeBytes,
    double UsagePercent,
    bool IsLocked,
    DateTimeOffset CreatedAt)
{
    public static UserDto From(User u) => new(
        u.Id, u.Email, u.DisplayName, u.UserRole.ToString(),
        u.QuotaBytes, u.UsedBytes, u.FreeBytes, u.UsagePercent,
        u.IsLocked, u.CreatedAt);
}
```

## Acceptance Criteria

- [ ] `GET /api/v1/users/me` → current user's profile
- [ ] `PUT /api/v1/users/me` with `{ displayName: "New Name" }` → updated profile
- [ ] `GET /api/v1/users` (Admin) → paginated list of all tenant users
- [ ] `PUT /api/v1/users/{id}/quota` (Admin) → updates `QuotaBytes`
- [ ] `POST /api/v1/users/{id}/lock` (Admin) → `IsLocked = true`
- [ ] `DELETE /api/v1/users/{id}/lock` (Admin) → `IsLocked = false`
- [ ] `UserDto` never contains `PasswordHash` or `LockedUntil` raw value

## Test Cases

- **TC-001**: `GET /users/me` → returns current user
- **TC-002**: `PUT /users/me` with new displayName → persisted
- **TC-003**: Non-admin `GET /users` → `403 Forbidden`
- **TC-004**: Admin `PUT /users/{id}/quota` → `QuotaBytes` updated
- **TC-005**: Admin `POST /users/{id}/lock` → account locked

## Implementation Tasks

- [ ] Create `UserEndpoints.cs` in `Strg.Api/Endpoints/`
- [ ] Create `UserDto` record
- [ ] Create `UpdateProfileRequest` and `UpdateQuotaRequest` records
- [ ] Register endpoints in `Program.cs`

## Testing Tasks

- [ ] Integration test: `GET /users/me` with JWT
- [ ] Integration test: non-admin `GET /users` → 403

## Security Review Checklist

- [ ] `PasswordHash` not in `UserDto`
- [ ] Admin endpoints require `Admin` policy
- [ ] `GET /users` scoped to current tenant (not all users in DB)

## Code Review Checklist

- [ ] `UserDto.From()` static factory (not constructor)
- [ ] Quota validation: `quotaBytes >= 0`

## Definition of Done

- [ ] All endpoints working with auth
