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
│   ├── Strg.GraphQL/           Hot Chocolate schema, types, resolvers
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
- `Strg.GraphQL` references: `Strg.Core`, Hot Chocolate
- `Strg.WebDav` references: `Strg.Core`, WebDav.Server
- `Strg.Api` references: all above (composition root)

No circular references. Domain interfaces live in `Strg.Core`; implementations in `Strg.Infrastructure`.

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
