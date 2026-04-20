# Identity & Authentication

## Overview

strg ships an **embedded OpenID Connect server** powered by [OpenIddict](https://github.com/openiddict/openiddict-core). This means:

- strg *is* the identity provider (IdP) — no external IdP required
- Other applications can use strg for SSO
- strg can federate with upstream providers: LDAP, Active Directory, Google OAuth, GitHub, SAML, etc.
- JWT tokens are issued, validated, and rotated by strg itself

---

## Token Flow

### Local users (password grant)

```
Client
  → POST /connect/token
    { grant_type: password, username, password, scope: files.read files.write }
  ← { access_token (15min), refresh_token (30d), token_type: Bearer }

Client → API request
  → Authorization: Bearer <access_token>
  ← response
```

### External identity (OIDC authorization code flow)

```
Browser
  → GET /connect/authorize?response_type=code&client_id=strg-web
  ← redirect to upstream IdP (Google, Keycloak, Authentik, etc.)
  ← upstream IdP redirects back: /connect/callback?code=...
  → POST /connect/token { grant_type: authorization_code, code: ... }
  ← { access_token, refresh_token }
```

### LDAP / Active Directory

```
Client → POST /connect/token { grant_type: password, username, password }
  → strg checks local user table first
  → if not found: tries configured LDAP connectors
    → LDAP bind with username/password
    → success → creates or updates user record
    → user groups fetched from LDAP → mapped to strg roles
  ← { access_token, refresh_token }
```

---

## OpenIddict Configuration

```csharp
services.AddOpenIddict()
    .AddCore(options =>
    {
        options.UseEntityFrameworkCore()
               .UseDbContext<StrgDbContext>();
    })
    .AddServer(options =>
    {
        options.SetTokenEndpointUris("/connect/token");
        options.SetAuthorizationEndpointUris("/connect/authorize");
        options.SetUserinfoEndpointUris("/connect/userinfo");
        options.SetIntrospectionEndpointUris("/connect/introspect");

        options.AllowPasswordFlow();          // local users, LDAP
        options.AllowAuthorizationCodeFlow(); // external IdP
        options.AllowRefreshTokenFlow();

        options.AddEphemeralEncryptionKey()   // dev
               .AddEphemeralSigningKey();     // dev

        // prod: load from X.509 cert or Vault
        options.AddEncryptionCertificate(cert);
        options.AddSigningCertificate(cert);

        options.UseAspNetCore()
               .EnableTokenEndpointPassthrough()
               .EnableAuthorizationEndpointPassthrough();
    })
    .AddValidation(options =>
    {
        options.UseLocalServer();
        options.UseAspNetCore();
    });
```

---

## Auth Connector Plugin Interface

External auth sources implement `IAuthConnector`:

```csharp
public interface IAuthConnector
{
    string ConnectorType { get; }  // 'ldap', 'saml', 'kerberos', 'radius', ...

    Task<AuthResult> AuthenticateAsync(AuthRequest request, CancellationToken ct);

    Task<IReadOnlyList<string>> GetGroupsAsync(
        string userId, CancellationToken ct);

    void ConfigureOpenIddict(OpenIddictServerBuilder builder);
}
```

Built-in: `LdapAuthConnector` (shipped with `Strg.Infrastructure`).
Plugins: SAML, Kerberos, RADIUS, proprietary SSO systems.

---

## Claims & Scopes

| Claim | Value |
|-------|-------|
| `sub` | User UUID |
| `email` | User email |
| `name` | Display name |
| `tenant_id` | Tenant UUID |
| `roles` | Array of role names |
| `quota_bytes` | User quota (for client use) |

| Scope | Grants |
|-------|--------|
| `files.read` | Read files, list drives, download |
| `files.write` | Upload, move, delete, create folders |
| `files.share` | Create and revoke shares |
| `tags.write` | Add and remove tags |
| `admin` | User management, drive config, audit log |
| `openid profile email` | Standard OIDC claims |

---

## strg as an IdP for Other Apps

strg can act as an SSO provider for external applications:

```
External App
  → GET /connect/authorize?client_id=my-app&response_type=code&scope=openid+profile
  ← strg login page (or silent SSO if already logged in)
  ← redirect to my-app with auth code
  → my-app exchanges code for token
  → my-app validates token against strg's JWKS: GET /.well-known/jwks.json
```

This enables strg to serve as the identity hub for an entire self-hosted ecosystem (Gitea, Nextcloud, Grafana, etc.).

---

## User Roles

| Role | Permissions |
|------|-------------|
| `superadmin` | All permissions, including user deletion, config, plugin management |
| `admin` | User management, drive creation, plugin install, audit log access |
| `user` | Access their own files and shared files; manage their own tags |
| `readonly` | Read-only access to explicitly shared resources |

Roles are stored as claims in the JWT. Fine-grained per-resource permissions are in the ACL table.
