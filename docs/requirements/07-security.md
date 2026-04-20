# Security Requirements

## Threat Model

### Assets
- User file contents
- User credentials and tokens
- Encryption keys
- Audit logs
- Configuration and secrets

### Threat Actors
- External attacker (unauthenticated internet access)
- Malicious authenticated user (attempting privilege escalation)
- Compromised plugin (in-process, see Plugin System doc)
- Kubernetes pod compromise (v0.3+ scope)

---

## SR-01: Authentication

- All API endpoints require a valid JWT Bearer token (except `/connect/token`, public share links, and health checks)
- Tokens are issued by the embedded OpenIddict OIDC server
- Token expiry: 15 minutes (access token), 30 days (refresh token)
- Refresh tokens are rotated on use
- Failed login attempts: lockout after 5 failures per IP per hour
- Account lockout: 5 failed attempts → 15 minute lockout; 10 failures → 1 hour lockout
- Multi-factor authentication: TOTP support via OpenIddict extension (v0.2)

---

## SR-02: Authorization

- Every file and folder operation checks the ACL before proceeding
- ACL evaluation is never bypassed — even internal services use the same ACL layer
- Permission cache: effective permissions cached per `(userId, resourceId)` with a 60-second TTL
- Cache invalidated immediately on any ACL change for the affected resource
- Quota check is performed before accepting any upload chunk (not just at completion)

---

## SR-03: Audit Logging

All of the following actions are written to `audit_entries`:

| Category | Events |
|----------|--------|
| Auth | login.success, login.failure, token.refresh, logout |
| Files | file.upload, file.download, file.delete, file.move, file.copy |
| Folders | folder.create, folder.delete, folder.move |
| Sharing | share.create, share.revoke, share.access |
| ACL | acl.grant, acl.revoke |
| Drives | drive.create, drive.delete, drive.config.change |
| Users | user.create, user.delete, user.quota.change, user.role.change |
| Plugins | plugin.install, plugin.uninstall, plugin.enable, plugin.disable |
| Backup | backup.start, backup.complete, backup.failure |
| Admin | admin.impersonate, config.change |

Audit entries are **append-only** — no updates or deletes are allowed via any API. Purge is only possible by a super-admin with a direct database operation (with audit log itself tracking the purge).

---

## SR-04: Encryption at Rest

- Optional per-drive encryption using AES-256-GCM
- The `EncryptingStorageProvider` wraps any `IStorageProvider`
- Each file's content is encrypted with a unique data encryption key (DEK)
- DEKs are encrypted with a key encryption key (KEK) and stored alongside the file metadata
- KEK sources (pluggable via `IKeyProvider`):
  - **Environment variable** (default, simplest)
  - **Kubernetes Secret** (v0.3)
  - **HashiCorp Vault** (plugin)
  - **Azure Key Vault** (plugin)
- Key rotation: all DEKs can be re-encrypted with a new KEK without re-encrypting file content

---

## SR-05: Transport Security

- HTTPS required for all endpoints (enforced at the reverse proxy: Caddy, Nginx, or Traefik)
- HSTS header set with `max-age=31536000; includeSubDomains`
- WebDAV is served over HTTPS only — HTTP WebDAV requests are redirected
- TUS upload chunks over HTTPS only
- mTLS between Kubernetes pods (v0.3 via Linkerd service mesh)

---

## SR-06: Rate Limiting

| Endpoint | Limit | Burst |
|----------|-------|-------|
| `POST /connect/token` | 10/min/IP | 3 |
| `PATCH /upload/*` (TUS) | 1000/min/user | 100 |
| `POST /graphql` | 300/min/user | 50 |
| `GET /drives/*/files/*/content` | 100/min/user | 20 |
| Admin GraphQL mutations | 60/min/user | 10 |

Returns `429 Too Many Requests` with `Retry-After` header.

Implementation: ASP.NET Core `RateLimiterMiddleware` with in-memory token bucket (v0.1) or Redis (v0.3 distributed).

---

## SR-07: Input Validation

- All API inputs validated with FluentValidation
- Path traversal blocked: any path containing `..`, `//`, or null bytes is rejected with `400`
- File names sanitized: control characters and OS-reserved names rejected
- Symlinks not followed in the local filesystem provider
- JSONB configuration columns validated against a JSON Schema before storage
- SQL injection: eliminated by EF Core parameterized queries (raw SQL is never used)
- XSS: not applicable to API-only server; enforced in UI layer when added

---

## SR-08: Dependency Security

- `dotnet list package --vulnerable` runs in CI and fails the build on high/critical CVEs
- Dependencies are pinned to specific versions in project files
- Dependabot or Renovate configured for automated PR updates
- Plugin packages have checksums verified before loading

---

## SR-09: Secrets Management

- No secrets in source code or Docker images
- Secrets sourced from environment variables (v0.1), Kubernetes Secrets (v0.3), or Vault
- Encryption keys never logged
- Database connection strings obfuscated in logs
- JWT signing keys rotated on schedule (configurable; default: 90 days)
