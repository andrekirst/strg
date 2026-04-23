---
id: STRG-012
title: Configure embedded OpenIddict OIDC server
milestone: v0.1
priority: critical
status: done
type: implementation
labels: [identity, security, auth]
depends_on: [STRG-004, STRG-011]
blocks: [STRG-013, STRG-014, STRG-015]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: large
---

# STRG-012: Configure embedded OpenIddict OIDC server

## Summary

Configure OpenIddict as the embedded OIDC authorization server in `Strg.Infrastructure`. strg IS the identity provider. This covers the core OpenIddict setup — password flow for local users, authorization code flow for external apps, and JWT signing.

## Background / Context

OpenIddict replaces an external IdP like Keycloak. It issues JWT access tokens and refresh tokens. All other API endpoints validate these tokens. See `docs/architecture/03-identity.md` for the full design.

## Technical Specification

### Packages: `OpenIddict.AspNetCore`, `OpenIddict.EntityFrameworkCore`

### Registration in `Strg.Infrastructure/Identity/OpenIddictConfiguration.cs`:

```csharp
public static class OpenIddictConfiguration
{
    public static IServiceCollection AddStrgOpenIddict(
        this IServiceCollection services,
        IConfiguration config,
        bool isDevelopment)
    {
        services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<StrgDbContext>())
            .AddServer(o =>
            {
                o.SetTokenEndpointUris("/connect/token");
                o.SetAuthorizationEndpointUris("/connect/authorize");
                o.SetUserinfoEndpointUris("/connect/userinfo");
                o.SetIntrospectionEndpointUris("/connect/introspect");
                o.SetRevocationEndpointUris("/connect/revoke");

                o.AllowPasswordFlow()
                 .AllowAuthorizationCodeFlow()
                 .AllowRefreshTokenFlow();

                o.RegisterScopes(
                    "files.read", "files.write", "files.share",
                    "tags.write", "admin",
                    Scopes.OpenId, Scopes.Profile, Scopes.Email);

                o.SetAccessTokenLifetime(TimeSpan.FromMinutes(15));
                o.SetRefreshTokenLifetime(TimeSpan.FromDays(30));
                o.SetRefreshTokenReuseLeeway(TimeSpan.FromSeconds(30));
                // Sliding window: each refresh token use resets the 30-day expiry
                o.DisableAccessTokenEncryption(); // tokens validated locally, encryption optional

                if (isDevelopment)
                    o.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
                else
                    o.AddEncryptionCertificate(LoadCertificate(config))
                     .AddSigningCertificate(LoadCertificate(config));

                o.UseAspNetCore()
                 .EnableTokenEndpointPassthrough()
                 .EnableAuthorizationEndpointPassthrough()
                 .EnableUserinfoEndpointPassthrough();
            })
            .AddValidation(o =>
            {
                o.UseLocalServer();
                o.UseAspNetCore();
            });

        return services;
    }
}
```

### External IdP extensibility (Open-Closed Principle):

Define `IExternalIdentityProvider` in `Strg.Core/Identity/` — allows future IdPs (Google, GitHub, LDAP) to be added without modifying core auth code:

```csharp
public interface IExternalIdentityProvider
{
    string ProviderName { get; }  // "google", "github", "ldap"
    Task<ExternalIdentityClaim?> AuthenticateAsync(string code, CancellationToken ct);
}
```

No concrete implementations in v0.1 — infrastructure only. LDAP deferred to v0.2+.

### Seed default application (strg CLI/API client) on startup:

```csharp
// Create a default OpenIddict application for the API itself
// (allows password flow clients without registering explicitly)
```

## Acceptance Criteria

- [ ] `POST /connect/token` with valid username/password returns JWT access token
- [ ] `POST /connect/token` with invalid credentials returns 400 with `invalid_grant`
- [ ] Access token expires in 15 minutes
- [ ] Refresh token: sliding 30-day window (each use resets the 30-day expiry — no hard cap in v0.1)
- [ ] Refresh token rotation enabled (old token invalidated on use)
- [ ] `GET /.well-known/openid-configuration` returns OIDC discovery document
- [ ] `GET /.well-known/jwks.json` returns public signing keys
- [ ] Scopes `files.read`, `files.write`, `files.share`, `tags.write`, `admin` are registered
- [ ] Development: ephemeral signing keys (no cert needed)
- [ ] Production: signing key from X.509 certificate or Kubernetes Secret
- [ ] OpenIddict tables created in database (via migration)
- [ ] Userinfo endpoint returns email, name, sub claims

## Test Cases

- **TC-001**: POST `/connect/token` with valid creds → 200 with `access_token`
- **TC-002**: POST `/connect/token` with wrong password → 400 `invalid_grant`
- **TC-003**: POST `/connect/token` with expired token on refresh → 400 `invalid_grant`
- **TC-004**: Use access token on protected endpoint → 200
- **TC-005**: Use expired access token → 401
- **TC-006**: GET `/.well-known/openid-configuration` → 200 with issuer, endpoints
- **TC-007**: GET `/.well-known/jwks.json` → 200 with JWK set
- **TC-008**: Refresh token used twice (reuse attack) → second use returns 400 (leeway respected but third use denied)

## Implementation Tasks

- [ ] Install OpenIddict packages in `Strg.Infrastructure` and `Strg.Api`
- [ ] Create `OpenIddictConfiguration.cs` extension method
- [ ] Create `TokenController.cs` (passthrough for password flow)
- [ ] Create `UserInfoController.cs` (passthrough for userinfo)
- [ ] Register OpenIddict in `Program.cs`
- [ ] Implement `IOpenIddictServerHandler` for password flow user validation (calls IUserRepository)
- [ ] Implement user claims principal builder (email, name, sub, roles, tenant_id)
- [ ] Add OpenIddict EF Core tables to migration
- [ ] Seed default API application in startup `IHostedService`

## Security Review Checklist

- [ ] Ephemeral keys only in development (not production)
- [ ] Signing certificate loaded from secure source (environment variable or file, not hardcoded)
- [ ] `client_secret` not logged
- [ ] Token endpoint rate limited (see STRG-083)
- [ ] Refresh token reuse leeway set to prevent timing attacks but not too long
- [ ] PKCE required for authorization code flow
- [ ] `client_secret` for public clients (browser apps) is not required — use PKCE only
- [ ] Token endpoint only accessible over HTTPS

## Code Review Checklist

- [ ] OpenIddict configuration is in a separate extension class (not inline in `Program.cs`)
- [ ] Certificate loading code handles file-not-found gracefully (clear error)
- [ ] Claims principal builder is testable (no static access)

## Definition of Done

- [ ] `POST /connect/token` works for local user login
- [ ] Token validates on protected API endpoint
- [ ] Integration tests pass
- [ ] Security review completed
