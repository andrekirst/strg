# strg — Platform Overview

## Vision

**strg** (pronounced "storage") is a fully self-hosted, open-source cloud storage platform designed to replace commercial solutions like Microsoft OneDrive, Google Drive, and Dropbox. It gives individuals, teams, and organizations complete ownership of their data, infrastructure, and roadmap.

## Goals

- **Full ownership**: No vendor lock-in. Every component is open-source (Apache 2.0) and self-hostable.
- **Extensibility**: A plugin system lets the community add storage backends, auth providers, search engines, and API extensions.
- **Performance**: High-throughput file transfers, resumable uploads, and streaming downloads without compromising correctness.
- **Security**: Audit logging, encryption at rest, rate limiting, and hardened API from the start — not as an afterthought.
- **Interoperability**: WebDAV protocol means Windows Explorer, macOS Finder, and Linux file managers can mount strg drives natively — zero client install required.
- **API-first**: A fully documented, versioned REST + GraphQL API is the foundation. UIs, sync clients, and integrations are built on top.

## Non-Goals (v0.1)

- No web UI (API-first; UI comes later as a separate sub-project)
- No desktop sync app in v0.1 (WebDAV covers the zero-install case)
- No multi-tenant mode in v0.1 (TenantId scaffolded but not enforced)
- No microservices (single binary for v0.1; Kubernetes in v0.3)

## Deployment Evolution

```
v0.1  Single binary (dotnet run)
  │   SQLite database, local filesystem backend
  │
v0.2  Docker + Docker Compose
  │   PostgreSQL, named volume storage
  │
v0.3  Kubernetes + Helm chart
      CloudNativePG operator, Linkerd mTLS, WAL-G backups
```

## Open Source

- **License**: Apache 2.0
- **Repository**: `github.com/andrekirst/strg`
- **Plugin marketplace**: Self-hostable NuGet-compatible registry + community marketplace
- **Contribution**: GitHub flow, DCO sign-off

## Relationship to Existing Solutions

strg is **not** a fork of Nextcloud, Seafile, or Owncloud. It is a greenfield project in C#/.NET 9, designed from the ground up with:
- A strongly-typed plugin contract system
- An embedded OIDC identity server (strg can serve as SSO for other applications)
- A hybrid REST + GraphQL API
- Innovative future features (AI auto-tagging, ActivityPub federation, IPFS backend)
