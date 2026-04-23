---
id: STRG-083
title: Implement account lockout brute-force protection
milestone: v0.1
priority: high
status: done
type: implementation
labels: [security, auth]
depends_on: [STRG-014, STRG-015]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-083: Implement account lockout brute-force protection

## Summary

Implement account lockout in the login flow: after 5 failed attempts, lock the account for 15 minutes; after 10 failed attempts, lock for 1 hour. Failed attempts reset on successful login. This is enforced in the `PasswordFlowHandler` from STRG-015.

## Technical Specification

### Lockout policy (constants in `Strg.Core/Domain/LockoutPolicy.cs`):

```csharp
public static class LockoutPolicy
{
    public const int SoftLockThreshold = 5;
    public const int HardLockThreshold = 10;
    public static readonly TimeSpan SoftLockDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan HardLockDuration = TimeSpan.FromHours(1);
}
```

### Updated `PasswordFlowHandler` logic:

```csharp
public async Task HandleAsync(OpenIddictServerEvents.HandleTokenRequestContext context)
{
    var username = context.Request.Username;
    var password = context.Request.Password;

    var user = await _userManager.FindByEmailAsync(username);

    // User enumeration prevention: same timing regardless of whether user exists
    if (user is null)
    {
        await Task.Delay(200); // constant-time response
        context.Reject(error: "invalid_grant", description: "Invalid credentials.");
        return;
    }

    // Check lockout
    if (user.IsLocked && user.LockedUntil > DateTimeOffset.UtcNow)
    {
        context.Reject(error: "invalid_grant",
            description: "Account temporarily locked due to multiple failed login attempts.");
        await _audit.LogAsync(user, "auth.locked_attempt");
        return;
    }

    // Verify password
    if (!_passwordHasher.Verify(password, user.PasswordHash))
    {
        user.FailedLoginAttempts++;
        if (user.FailedLoginAttempts >= LockoutPolicy.HardLockThreshold)
            user.LockedUntil = DateTimeOffset.UtcNow + LockoutPolicy.HardLockDuration;
        else if (user.FailedLoginAttempts >= LockoutPolicy.SoftLockThreshold)
            user.LockedUntil = DateTimeOffset.UtcNow + LockoutPolicy.SoftLockDuration;

        await _db.SaveChangesAsync();
        context.Reject(error: "invalid_grant", description: "Invalid credentials.");
        return;
    }

    // Successful login: reset counter
    user.FailedLoginAttempts = 0;
    user.LockedUntil = null;
    user.LastLoginAt = DateTimeOffset.UtcNow;
    await _db.SaveChangesAsync();

    // ... build claims principal ...
}
```

## Acceptance Criteria

- [ ] 5 failed logins → account locked for 15 minutes
- [ ] 10 failed logins → account locked for 1 hour
- [ ] Successful login resets `FailedLoginAttempts` to 0
- [ ] Login with non-existent user → same error message as wrong password (user enumeration prevention)
- [ ] Locked account → error message mentions lockout (no credentials hint)
- [ ] Lock expiry: after `LockedUntil` passes → next attempt allowed

## Test Cases

- **TC-001**: 5 failed logins → 6th attempt returns locked error
- **TC-002**: Successful login after 3 fails → `FailedLoginAttempts` reset to 0
- **TC-003**: Login with unknown email → same error message as wrong password
- **TC-004**: Account locked → wait for lock duration → login succeeds with correct password
- **TC-005**: 10 failed logins → locked for 1 hour (not 15 minutes)

## Implementation Tasks

- [ ] Create `LockoutPolicy.cs` constants in `Strg.Core/Domain/`
- [ ] Update `PasswordFlowHandler` with lockout check before password verification
- [ ] Add constant-time delay for user-not-found path
- [ ] Log audit entry for locked account attempt

## Testing Tasks

- [ ] Integration test: 5 failed logins → lockout applied
- [ ] Integration test: successful login resets counter
- [ ] Unit test: lockout duration threshold logic

## Security Review Checklist

- [ ] Same error message for wrong password AND unknown user (prevents user enumeration)
- [ ] Lockout state stored in DB (survives process restart)
- [ ] Audit entry logged for locked account access attempts
- [ ] Constant-time response for unknown users (resist timing attacks)

## Code Review Checklist

- [ ] Lockout check happens BEFORE password verification (avoid unnecessary hash computation)
- [ ] `FailedLoginAttempts` incremented BEFORE `SaveChangesAsync` (not after)

## Definition of Done

- [ ] Lockout triggers after 5 failed attempts
- [ ] Counter resets on success
