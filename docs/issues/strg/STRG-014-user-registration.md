---
id: STRG-014
title: Implement local user registration and password management
milestone: v0.1
priority: critical
status: open
type: implementation
labels: [identity, auth, api]
depends_on: [STRG-011, STRG-012]
blocks: [STRG-015]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-014: Implement local user registration and password management

## Summary

Implement user registration (public self-registration) and password management. First-run creates a superadmin user when no users exist and prints the generated password to stdout.

## Technical Specification

### File: `src/Strg.Core/Identity/IUserManager.cs`

```csharp
public interface IUserManager
{
    Task<Result<User>> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default);
    Task<Result> SetPasswordAsync(Guid userId, string newPassword, CancellationToken ct = default);
    Task<Result> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken ct = default);
    Task<bool> ValidatePasswordAsync(Guid userId, string password, CancellationToken ct = default);
    Task RecordFailedLoginAsync(Guid userId, CancellationToken ct = default);
    Task ResetFailedLoginsAsync(Guid userId, CancellationToken ct = default);
}
```

### Password hashing: Use `IPasswordHasher<User>` from ASP.NET Core Identity (PBKDF2).

### First-run initialization:

On startup, if no users exist, create:
- Email: `admin@strg.local`
- Password: random 24-char password (logged to stdout ONCE, marked clearly)
- Role: `SuperAdmin`

### `CreateUserRequest`:

```csharp
public record CreateUserRequest(
    string Email,
    string DisplayName,
    string Password,
    UserRole Role = UserRole.User,
    long? QuotaBytes = null);
```

## Acceptance Criteria

- [ ] `CreateUserAsync` creates a user in both `users` table and OpenIddict's user store
- [ ] Password is hashed with PBKDF2 (never stored in plaintext)
- [ ] Email is validated (format check, uniqueness within tenant)
- [ ] `ValidatePasswordAsync` returns false for wrong password
- [ ] `RecordFailedLoginAsync` increments `FailedLoginAttempts` and sets `LockedUntil` after 5 failures
- [ ] After 5 failures: `LockedUntil = now + 15 minutes`
- [ ] After 10 failures: `LockedUntil = now + 1 hour`
- [ ] First-run admin user created if no users exist
- [ ] First-run admin password printed to stdout with clear warning
- [ ] Creating a user with an existing email returns `Result.Failure` (not exception)

## Test Cases

- **TC-001**: Create user with valid data → user exists in DB
- **TC-002**: Create user with duplicate email → `Result.Failure` with `EmailAlreadyExists` error
- **TC-003**: `ValidatePasswordAsync` with correct password → `true`
- **TC-004**: `ValidatePasswordAsync` with wrong password → `false`
- **TC-005**: 5 consecutive `RecordFailedLoginAsync` → `User.IsLocked == true`, `LockedUntil ≈ now + 15min`
- **TC-006**: Login attempt on locked account → `false` from `ValidatePasswordAsync`
- **TC-007**: Password contains less than 12 characters → `CreateUserAsync` returns `Result.Failure`

## Implementation Tasks

- [ ] Create `src/Strg.Core/Identity/IUserManager.cs`
- [ ] Create `src/Strg.Infrastructure/Identity/UserManager.cs`
- [ ] Implement password hashing via `IPasswordHasher<User>`
- [ ] Implement lockout logic in `RecordFailedLoginAsync`
- [ ] Create `FirstRunInitializationService : BackgroundService`
- [ ] Write unit tests for lockout logic
- [ ] Write integration tests for create user + validate password

## Security Review Checklist

- [ ] Passwords never logged (not even debug level)
- [ ] PBKDF2 with at least 100,000 iterations (ASP.NET Core default uses 600,000 for V3)
- [ ] Password minimum length is enforced (≥ 12 characters recommended)
- [ ] Email comparison is case-insensitive
- [ ] `ValidatePasswordAsync` takes constant time (use `IPasswordHasher.VerifyHashedPassword`)
- [ ] First-run password is printed to stdout ONCE and never stored in logs
- [ ] Failed login counter is server-side only (not client-visible)

## Code Review Checklist

- [ ] `Result<T>` pattern used (not exceptions for validation failures)
- [ ] All async methods accept `CancellationToken`
- [ ] Business logic in `IUserManager` implementation, not in OpenIddict handlers

## Definition of Done

- [ ] User creation and password validation work end-to-end
- [ ] Lockout logic tested and passing
- [ ] First-run admin user created on fresh database
