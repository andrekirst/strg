# strg

A self-hosted, open-source cloud storage platform — a full-ownership alternative to Microsoft OneDrive, Google Drive, and Dropbox.

## Philosophy

- **Full ownership**: Your data, your server, your code. Apache 2.0.
- **API-first**: A hardened REST + GraphQL API is the foundation. UIs are plugins.
- **Pluggable everything**: Storage backends, auth providers, search engines, and API extensions via a typed plugin system.
- **Interoperable**: WebDAV lets Windows Explorer, macOS Finder, and Linux file managers mount drives natively — zero install required.

## Features

- **Named drives** — Mount local FS, network shares, S3, Google Drive, OneDrive, SFTP, IPFS via plugins
- **Resumable uploads** — TUS protocol; resume interrupted transfers from the exact byte
- **WebDAV** — OS-native file system mounting (Windows, macOS, Linux, iOS, Android)
- **Embedded OIDC** — strg is an identity provider. Federate upstream: LDAP, Active Directory, Google, SAML
- **Key-value tags** — User-scoped `key=value` tags on files and folders; queryable via GraphQL
- **Full ACL** — Users, groups, roles, public links, expiry, per-operation permissions
- **File versioning** — Per-drive retention policies (count, days, unlimited)
- **Archive management** — Browse ZIP files as virtual folders, extract, compress, stream download
- **Outbox events** — Reliable async processing without an external message broker
- **Audit logging** — Every operation logged; append-only; queryable via admin API
- **Encryption at rest** — AES-256-GCM per-file, pluggable key management

## Roadmap Highlights

- **AI auto-tagging** — LLM-powered tag suggestions on upload
- **Semantic search** — Natural language file search via RAG + pgvector
- **ActivityPub federation** — Share drives between strg instances like Mastodon shares posts
- **IPFS backend** — Content-addressed storage with automatic deduplication

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Language | C# / .NET 9 |
| API (file I/O) | ASP.NET Core + TUS (tusdotnet) + WebDAV |
| API (metadata) | GraphQL — Hot Chocolate |
| Auth | OpenIddict (embedded OIDC server) |
| Database | SQLite (dev) / PostgreSQL (prod) |
| ORM | Entity Framework Core |
| Events | MassTransit Outbox (no broker required) |
| Deployment | Single binary → Docker → Kubernetes (Helm) |

## Quick Start

```bash
git clone https://github.com/andrekirst/strg
cd strg
dotnet run --project src/Strg.Api
```

Opens on `https://localhost:5000`. GraphQL playground at `/graphql`. WebDAV at `/dav/`.

## Documentation

- [Platform Overview](docs/requirements/01-overview.md)
- [Functional Requirements](docs/requirements/02-functional-requirements.md)
- [API Design](docs/requirements/04-api-design.md)
- [Data Model](docs/requirements/05-data-model.md)
- [Plugin System](docs/requirements/06-plugin-system.md)
- [Security](docs/requirements/07-security.md)
- [System Architecture](docs/architecture/01-system-overview.md)
- [Storage Abstraction](docs/architecture/02-storage-abstraction.md)
- [Identity & Auth](docs/architecture/03-identity.md)
- [Event System](docs/architecture/04-event-system.md)
- [Deployment](docs/architecture/05-deployment.md)

## License

Apache 2.0 — see [LICENSE](LICENSE).
