---
id: STRG-016
title: Implement token refresh and revocation endpoints
milestone: v0.1
priority: high
status: open
type: implementation
labels: [identity, auth]
depends_on: [STRG-015]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-016: Implement token refresh and revocation endpoints

## Summary

Implement the OpenIddict passthrough handlers for `grant_type=refresh_token` (issues new access token) and token revocation (`POST /connect/revoke`). Both must update audit logs.

## Acceptance Criteria

- [ ] `POST /connect/token { grant_type=refresh_token, refresh_token=... }` returns a new access token and a rotated refresh token
- [ ] Old refresh token is invalidated after use (rotation)
- [ ] Using the same refresh token twice (replay) → 400 within the reuse leeway window
- [ ] `POST /connect/revoke { token=... }` invalidates the token
- [ ] After revocation, the token cannot be used for refresh or API access
- [ ] `token.refresh` audit entry created on successful refresh
- [ ] `token.revoke` audit entry created on revocation

## Test Cases

- **TC-001**: Valid refresh token → new access token + new refresh token
- **TC-002**: Old refresh token after rotation → 400 (within leeway) or 400 (outside leeway)
- **TC-003**: Revoke access token → subsequent API request with that token → 401
- **TC-004**: Revoke refresh token → subsequent refresh attempt → 400

## Implementation Tasks

- [ ] Create `RefreshTokenFlowHandler.cs` (validates refresh token, builds new principal)
- [ ] Register revocation endpoint in OpenIddict config (already done in STRG-012)
- [ ] Add audit logging for refresh and revocation events
- [ ] Write integration tests

## Security Review Checklist

- [ ] Refresh token rotation prevents token replay attacks
- [ ] Revoked tokens are checked on every request (OpenIddict handles this)
- [ ] Reuse leeway is short (30 seconds configured in STRG-012)

## Definition of Done

- [ ] Refresh and revocation work in integration tests
- [ ] Audit entries created for both events
