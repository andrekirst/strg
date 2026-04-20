---
id: STRG-086
title: Implement user registration REST endpoint
milestone: v0.1
priority: high
status: open
type: implementation
labels: [api, auth, users]
depends_on: [STRG-014, STRG-013]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-086: Implement user registration REST endpoint

## Summary

Implement `POST /api/v1/users/register` for new user self-registration. In v0.1 self-registration is enabled by default (configurable via `appsettings.json`). First user registered becomes admin automatically.

## Technical Specification

### Route: `POST /api/v1/users/register`

### Request body:

```json
{
  "email": "user@example.com",
  "password": "MySecurePassword123!",
  "displayName": "Alice"
}
```

### Response (`201 Created`):

```json
{
  "id": "uuid",
  "email": "user@example.com",
  "displayName": "Alice",
  "role": "User",
  "createdAt": "2024-01-01T00:00:00Z"
}
```

### Handler:

```csharp
app.MapPost("/api/v1/users/register", async (
    RegisterUserRequest request,
    [FromServices] IUserManager userManager,
    [FromServices] IValidator<RegisterUserRequest> validator,
    CancellationToken ct) =>
{
    var validation = await validator.ValidateAsync(request, ct);
    if (!validation.IsValid)
        return Results.ValidationProblem(validation.ToDictionary());

    var result = await userManager.CreateAsync(new CreateUserRequest(
        request.Email, request.Password, request.DisplayName), ct);

    return result.IsSuccess
        ? Results.Created($"/api/v1/users/{result.User!.Id}", UserDto.From(result.User))
        : Results.Problem(result.Error, statusCode: result.StatusCode);
})
// NOTE: No rate limiting on registration — public self-registration, quota is the guard
.WithOpenApi();
```

### Validation rules (`RegisterUserRequest`):

- `email`: valid email format, max 255 chars
- `password`: min 8 chars, at least 1 uppercase, 1 digit, 1 special char
- `displayName`: max 255 chars, not empty

### First-user-is-admin logic (in `IUserManager.CreateAsync`):

```csharp
var isFirstUser = !await _db.Users.AnyAsync(ct);
var role = isFirstUser ? UserRole.Admin : UserRole.User;
```

### Self-registration gate:

```json
{
  "Registration": {
    "Enabled": true,
    "AllowedEmailDomains": []  // empty = any domain allowed
  }
}
```

## Acceptance Criteria

- [ ] `POST /api/v1/users/register` with valid data → `201 Created`
- [ ] Duplicate email → `409 Conflict`
- [ ] Weak password → `400 Bad Request` with password policy explanation
- [ ] First user registered → `UserRole.Admin`
- [ ] `Registration:Enabled = false` in config → `403 Forbidden`
- [ ] **No rate limiting** on this endpoint — public self-registration enabled by default; quota (10GB/user) is the primary abuse guard

## Test Cases

- **TC-001**: Register → `201`, can login with new credentials
- **TC-002**: Duplicate email → `409`
- **TC-003**: Password `"short"` → `400` with password rule errors
- **TC-004**: First user → admin role
- **TC-005**: `Registration:Enabled = false` → `403`

## Implementation Tasks

- [ ] Add `MapPost("/api/v1/users/register")` in `Program.cs`
- [ ] Create `RegisterUserRequest` record
- [ ] Create `RegisterUserRequestValidator` with email + password rules
- [ ] Update `IUserManager.CreateAsync` with first-user-is-admin logic
- [ ] Add `Registration:Enabled` config check

## Testing Tasks

- [ ] Integration test: register → login → get token
- [ ] Integration test: duplicate email → 409
- [ ] Integration test: weak password → 400

## Security Review Checklist

- [ ] No rate limiting applied (confirmed design decision)
- [ ] Password stored as PBKDF2 hash (never plaintext)
- [ ] Response never echoes back the password
- [ ] `AllowedEmailDomains` filter (optional, for corporate deployments)

## Code Review Checklist

- [ ] Validator uses regex for password complexity (not manual checks)
- [ ] `Results.Created` sets the `Location` header

## Definition of Done

- [ ] Registration endpoint working
- [ ] First user gets admin role
