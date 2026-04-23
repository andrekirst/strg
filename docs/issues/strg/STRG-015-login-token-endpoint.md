---
id: STRG-015
title: Implement login endpoint (POST /connect/token) with brute-force protection
milestone: v0.1
priority: critical
status: done
type: implementation
labels: [identity, auth, api, security]
depends_on: [STRG-012, STRG-013, STRG-014]
blocks: [STRG-016]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-015: Implement login endpoint (POST /connect/token) with brute-force protection

## Summary

Implement the OpenIddict passthrough handler for the password flow token endpoint. The handler validates credentials against the local user store, applies lockout logic, builds the claims principal, and issues tokens.

## Technical Specification

### File: `src/Strg.Infrastructure/Identity/PasswordFlowHandler.cs`

This is an `IOpenIddictServerHandler<OpenIddictServerEvents.HandleTokenRequestContext>` that processes `grant_type=password` requests.

```csharp
public class PasswordFlowHandler : IOpenIddictServerHandler<HandleTokenRequestContext>
{
    public async ValueTask HandleAsync(HandleTokenRequestContext context)
    {
        if (!context.Request.IsPasswordGrantType()) return;

        var user = await userRepository.GetByEmailAsync(tenantId, context.Request.Username!);
        if (user is null || !await userManager.ValidatePasswordAsync(user.Id, context.Request.Password!))
        {
            if (user is not null)
                await userManager.RecordFailedLoginAsync(user.Id);
            context.Reject(error: Errors.InvalidGrant, description: "Invalid credentials");
            return;
        }

        if (user.IsLocked)
        {
            context.Reject(error: Errors.InvalidGrant, description: "Account locked");
            return;
        }

        await userManager.ResetFailedLoginsAsync(user.Id);
        await auditService.LogAsync("login.success", user.Id);

        var principal = BuildPrincipal(user, context.Request.GetScopes());
        context.SignIn(principal);
    }
}
```

### Claims in the principal:
- `sub`: `user.Id.ToString()`
- `email`: `user.Email`
- `name`: `user.DisplayName`
- `role`: `user.Role.ToString()`
- `tenant_id`: `user.TenantId.ToString()`
- `quota_bytes`: `user.QuotaBytes.ToString()`

## Acceptance Criteria

- [ ] `POST /connect/token { grant_type=password, username=..., password=... }` returns JWT
- [ ] Invalid credentials → 400 `invalid_grant` (no info about whether user exists)
- [ ] Locked account → 400 `invalid_grant` (same error code, not 423)
- [ ] Successful login → `FailedLoginAttempts` reset to 0
- [ ] Successful login → `login.success` audit entry created
- [ ] Failed login → `login.failure` audit entry created (with IP)
- [ ] JWT contains all expected claims (sub, email, name, role, tenant_id)
- [ ] Token lifetime matches OpenIddict configuration (15 min access, 30 day refresh)
- [ ] Username/password sent as form-encoded body (not JSON)

## Test Cases

- **TC-001**: Correct credentials → 200 with `access_token`, `refresh_token`, `token_type=Bearer`
- **TC-002**: Wrong password → 400 `{"error":"invalid_grant"}`
- **TC-003**: Non-existent user → 400 `{"error":"invalid_grant"}` (same error as wrong password)
- **TC-004**: Correct credentials, account locked → 400 `{"error":"invalid_grant"}`
- **TC-005**: Check JWT claims after successful login → sub, email, name, role, tenant_id present
- **TC-006**: Check audit_entries after login success → entry with action `login.success`
- **TC-007**: Check audit_entries after login failure → entry with action `login.failure`

## Implementation Tasks

- [ ] Create `PasswordFlowHandler.cs`
- [ ] Build claims principal helper method
- [ ] Integrate with `IUserManager.ValidatePasswordAsync`
- [ ] Integrate with `IUserManager.RecordFailedLoginAsync`
- [ ] Integrate with audit service
- [ ] Register handler in OpenIddict pipeline
- [ ] Write integration tests using test HTTP client

## Security Review Checklist

- [ ] Error message for wrong password and non-existent user is identical (prevents user enumeration)
- [ ] Locked account error message is identical to wrong password (prevents timing attack on lockout state)
- [ ] IP address captured from `X-Forwarded-For` (behind reverse proxy) for audit log
- [ ] Rate limiting on token endpoint applies (configured in STRG-083)
- [ ] No credentials in response body beyond the token
- [ ] Token endpoint requires HTTPS (configured by STRG-010)

## Code Review Checklist

- [ ] Handler is registered correctly in OpenIddict's handler chain
- [ ] `BuildPrincipal` is a private method or separate class (not inline)
- [ ] `context.Reject()` is called (not exception thrown)

## Definition of Done

- [ ] Login works end-to-end in integration test
- [ ] Security review completed
- [ ] Audit entries verified
