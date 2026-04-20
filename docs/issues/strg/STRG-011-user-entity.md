---
id: STRG-011
title: Create User domain entity and role model
milestone: v0.1
priority: critical
status: open
type: implementation
labels: [core, domain, identity]
depends_on: [STRG-003]
blocks: [STRG-004, STRG-012, STRG-019]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-011: Create User domain entity and role model

## Summary

Create the `User` domain entity in `Strg.Core`, the `UserRole` enum, and the `IUserRepository` interface. This is the domain-layer representation of a user — distinct from OpenIddict's identity model.

## Technical Specification

### File: `src/Strg.Core/Domain/User.cs`

```csharp
namespace Strg.Core.Domain;

public sealed class User : TenantedEntity
{
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public long QuotaBytes { get; set; } = 10L * 1024 * 1024 * 1024; // 10 GB default
    public long UsedBytes { get; set; }
    public UserRole Role { get; set; } = UserRole.User;
    public DateTimeOffset? LockedUntil { get; set; }
    public int FailedLoginAttempts { get; set; }

    public bool IsLocked => LockedUntil.HasValue && LockedUntil > DateTimeOffset.UtcNow;
    public long FreeBytes => Math.Max(0, QuotaBytes - UsedBytes);
    public double UsagePercent => QuotaBytes == 0 ? 0 : (double)UsedBytes / QuotaBytes * 100;
}

public enum UserRole
{
    Readonly = 0,
    User = 1,
    Admin = 2,
    SuperAdmin = 3
}
```

### File: `src/Strg.Core/Domain/IUserRepository.cs`

```csharp
public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<User?> GetByEmailAsync(Guid tenantId, string email, CancellationToken ct = default);
    Task<IReadOnlyList<User>> ListAsync(Guid tenantId, CancellationToken ct = default);
    Task AddAsync(User user, CancellationToken ct = default);
    Task UpdateAsync(User user, CancellationToken ct = default);
    Task SoftDeleteAsync(Guid id, CancellationToken ct = default);
}
```

## Acceptance Criteria

- [ ] `User` entity inherits from `TenantedEntity`
- [ ] `User.Email` is required and stored in lowercase
- [ ] `User.QuotaBytes` defaults to 10 GB
- [ ] `User.IsLocked` returns `true` when `LockedUntil` is in the future
- [ ] `User.FreeBytes` never returns a negative value
- [ ] `UserRole` enum exists with Readonly, User, Admin, SuperAdmin values
- [ ] `IUserRepository` defined in `Strg.Core.Domain`
- [ ] Zero external package references added to `Strg.Core`

## Test Cases

- **TC-001**: `User { QuotaBytes = 100, UsedBytes = 150 }.FreeBytes == 0` (not negative)
- **TC-002**: `User { LockedUntil = past }.IsLocked == false`
- **TC-003**: `User { LockedUntil = future }.IsLocked == true`
- **TC-004**: `User.UsagePercent` with `QuotaBytes = 0` → returns 0 (no divide-by-zero)

## Implementation Tasks

- [ ] Create `src/Strg.Core/Domain/User.cs`
- [ ] Create `src/Strg.Core/Domain/UserRole.cs`
- [ ] Create `src/Strg.Core/Domain/IUserRepository.cs`
- [ ] Write unit tests in `Strg.Core.Tests/Domain/UserTests.cs`

## Security Review Checklist

- [ ] No password hash stored in `User` entity (passwords are OpenIddict's concern)
- [ ] `FailedLoginAttempts` cannot be decremented by non-admin code paths (business logic)
- [ ] `UserRole.SuperAdmin` is documented as a single-user role (system-level)

## Code Review Checklist

- [ ] `Email` stored in a case-insensitive way (lowercase on set, or use collation in DB)
- [ ] `FreeBytes` computed property is safe for edge cases (QuotaBytes = 0, UsedBytes > QuotaBytes)
- [ ] Repository interface follows the pattern of other repositories in the codebase

## Definition of Done

- [ ] `User.cs`, `UserRole.cs`, `IUserRepository.cs` created
- [ ] Unit tests pass
- [ ] Zero new package dependencies in Strg.Core
