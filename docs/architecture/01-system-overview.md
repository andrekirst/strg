# System Architecture Overview

## High-Level Architecture

```
                          Clients
                             │
              ┌──────────────┼──────────────┐
              │              │              │
        WebDAV clients    REST/GraphQL    Future
        (Windows Explorer  (CLI, apps,    (sync app,
        macOS Finder)      integrations)   web UI)
              │              │
              └──────────────┘
                       │
              ┌────────▼────────┐
              │   Reverse Proxy  │  (Caddy / Nginx / Traefik)
              │   TLS termination│
              └────────┬────────┘
                       │
         ┌─────────────▼──────────────────────────────┐
         │              strg-api                        │
         │                                              │
         │  ┌──────────┐ ┌──────────┐ ┌────────────┐  │
         │  │  WebDAV   │ │REST+TUS  │ │  GraphQL   │  │
         │  │ /dav/*    │ │ /api/v1/ │ │ /graphql   │  │
         │  └─────┬─────┘ └────┬─────┘ └─────┬──────┘  │
         │        └────────────┴─────────────┘          │
         │                     │                         │
         │        ┌────────────▼────────────┐           │
         │        │      Domain Layer        │           │
         │        │  FileService             │           │
         │        │  DriveService            │           │
         │        │  AuthService             │           │
         │        │  TagService              │           │
         │        │  QuotaService            │           │
         │        └────────────┬────────────┘           │
         │                     │                         │
         │   ┌─────────────────┼─────────────────┐      │
         │   │                 │                 │      │
         │  ┌▼──────────┐ ┌───▼────────┐ ┌──────▼────┐ │
         │  │IStorage    │ │ OpenIddict │ │  Outbox   │ │
         │  │Provider    │ │ OIDC server│ │  Events   │ │
         │  └─────┬──────┘ └────────────┘ └──────┬────┘ │
         │        │                               │      │
         │  ┌─────┴──────────────────┐            │      │
         │  │  Local FS  │  (plugins) │            │      │
         │  │  provider  │  S3, GDrv  │            │      │
         │  └────────────┴───────────┘            │      │
         └───────────────────────────────────────┘       │
                       │              │
              ┌─────────▼──┐    ┌─────▼─────┐
              │ PostgreSQL  │    │ File Store │
              │ (metadata,  │    │ (blobs on  │
              │  EF Core)   │    │  provider) │
              └─────────────┘    └───────────┘
```

---

## Project Structure

```
strg/
├── src/
│   ├── Strg.Core/              Domain entities, interfaces, no infra deps
│   │   ├── Domain/             Entities: Drive, FileItem, User, Tag, ...
│   │   ├── Storage/            IStorageProvider, IStorageFile, IStorageDirectory
│   │   ├── Identity/           IUserManager, IAuthConnector
│   │   ├── Plugins/            IStrgPlugin + all plugin interfaces
│   │   └── Services/           FileService, DriveService, QuotaService, ...
│   │
│   ├── Strg.Infrastructure/    EF Core, providers, OpenIddict, MassTransit
│   │   ├── Data/               StrgDbContext, migrations, repositories
│   │   ├── Storage/            LocalFileSystemProvider, EncryptingStorageProvider
│   │   ├── Identity/           OpenIddict config, LdapConnector
│   │   └── Events/             MassTransit outbox, event handlers
│   │
│   ├── Strg.GraphQl/           Hot Chocolate schema, types, resolvers
│   │   ├── Types/              FileType, DriveType, TagType, UserType, ...
│   │   ├── Queries/            FileQueries, DriveQueries, SearchQueries
│   │   ├── Mutations/          FileMutations, TagMutations, ShareMutations
│   │   └── Subscriptions/      FileSubscriptions, BackupSubscriptions
│   │
│   ├── Strg.WebDav/            WebDAV RFC 4918 server
│   │   ├── StrgWebDavStore.cs  Bridges WebDAV ops to IStorageProvider
│   │   └── StrgWebDavAuth.cs   Basic auth → JWT exchange
│   │
│   └── Strg.Api/               ASP.NET Core host
│       ├── Program.cs          App builder + middleware pipeline
│       ├── appsettings.json    Configuration schema
│       └── Endpoints/          REST file endpoints (download, TUS)
│
├── tests/
│   ├── Strg.Core.Tests/        Unit tests (no infrastructure)
│   ├── Strg.Api.Tests/         API integration tests (TestServer + SQLite)
│   └── Strg.Integration.Tests/ End-to-end tests (real HTTP + WebDAV client)
│
├── plugins/                    Optional: pre-built plugin DLLs
├── deploy/
│   ├── Dockerfile
│   ├── docker-compose.yml
│   └── helm/                   (v0.3)
│
└── docs/
    ├── requirements/
    ├── architecture/
    └── decisions/
```

---

## Dependency Rules

- `Strg.Core` references: nothing (no NuGet packages except .NET BCL)
- `Strg.Infrastructure` references: `Strg.Core`, EF Core, OpenIddict, MassTransit
- `Strg.GraphQl` references: `Strg.Core`, Hot Chocolate
- `Strg.WebDav` references: `Strg.Core`, WebDav.Server
- `Strg.Api` references: all above (composition root)

No circular references. Domain interfaces live in `Strg.Core`; implementations in `Strg.Infrastructure`.

---

## Middleware Pipeline Order (STRG-010)

`Program.cs` registers ASP.NET Core middleware in the order below. The order is
load-bearing: rate limiting MUST run before authentication (otherwise a brute-force
attacker can exhaust auth-handler CPU without hitting the limiter), security
headers MUST be registered before short-circuiting middleware (Swashbuckle, the
`/dav` branch) or those responses will not carry the headers.

| # | Middleware                        | Why this slot                                                                                   |
|---|-----------------------------------|-------------------------------------------------------------------------------------------------|
| 0 | `ConfigureKestrel(AddServerHeader=false)` | Host-level setting — suppresses Kestrel's default `Server: Kestrel` response header.      |
| 1 | `UseHttpsRedirection`             | Top of pipeline so every downstream sees the upgraded scheme.                                   |
| 2 | `UseHsts` (production only)       | Env-gated because the header is browser-cached for a year and `UseHsts` excludes loopback.      |
| 3 | `UseRouting`                      | Required before endpoint-specific `RequireRateLimiting` metadata can be consulted.              |
| 4 | `UseStrgSecurityHeaders`          | Before `/dav` and `UseStrgOpenApi` — both short-circuit and would bypass later header middleware. |
| 5 | `app.Map("/dav", ...)`            | WebDAV branch with its own auth pipeline. Unchanged from STRG-067/073.                          |
| 6 | `UseStrgOpenApi`                  | Before `UseAuthentication` so spec/UI stay anonymous; after security headers so spec gets them. |
| 7 | `UseCors("strg-cors")`            | After `/dav` branch so WebDAV OPTIONS is not intercepted by CORS preflight (RFC 4918 §10.1).    |
| 8 | `UseSerilogRequestLogging`        | After CORS so log lines reflect the resolved CORS outcome; before auth so 401s are still logged. |
| 9 | `UseRateLimiter`                  | Before `UseAuthentication` (security-review: "rate limit before auth").                         |
| 10 | `UseAuthentication`              |                                                                                                 |
| 11 | `UseAuthorization`               |                                                                                                 |
| 12 | `UseWebSockets`                  |                                                                                                 |
| 13 | `MapPrometheusScrapingEndpoint("/metrics")` + `AllowAnonymous().DisableRateLimiting()` | `/metrics` bypasses auth and rate limiting.                   |
| 14 | `MapHealthChecks("/health/live" \| "/health/ready")` + `AllowAnonymous().DisableRateLimiting()` | Health probes bypass rate limiting.                           |
| 15 | `MapGraphQL` / token / userinfo / drive / registration endpoints | Named policies attach here (e.g. `RequireRateLimiting("auth")` on `/connect/token`). |

Anything added to this pipeline MUST be placed in a slot consistent with the
table above — see the inline XML comments in `Program.cs` for per-slot rationale.

---

## Request Lifecycle: File Upload

```
1. Client sends TUS initiation: POST /upload
2. strg.Api validates JWT → extracts userId, tenantId
3. QuotaService.CheckAsync(userId, fileSize) → throws if quota exceeded
4. TUS store creates upload session → stores offset in DB
5. Client sends chunks: PATCH /upload/{id}
6. Each chunk → LocalFileSystemProvider.WriteChunkAsync()
7. On final chunk:
   a. FileService.CompleteUploadAsync() begins DB transaction
   b. INSERT file_items (metadata)
   c. INSERT file_versions (version 1)
   d. UPDATE user.used_bytes += fileSize
   e. INSERT outbox_events { type: 'file.uploaded', payload: { fileId, driveId } }
   f. COMMIT transaction
8. Outbox poller picks up event → dispatches to handlers:
   a. SearchIndexHandler → indexes file in search provider
   b. AiTaggerHandler → requests tag suggestions (if plugin installed)
   c. QuotaNotificationHandler → checks if user > 80% quota
```
