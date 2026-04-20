# strg Issue Tracker

All issues are defined in Markdown files in this directory. Each issue is designed as an atomic task for a Claude Code agent.

## Issue Naming Convention

`{PREFIX}-{NUMBER}-{slug}.md`

- **CC-xxx**: Claude Code setup issues (CLAUDE.md, hooks, agent teams)
- **STRG-xxx**: strg implementation issues

---

## Milestone Overview

| Milestone | Goal |
|---|---|
| **v0.1** | Single-binary runnable: REST + GraphQL + WebDAV + auth + events |
| **v0.2** | Docker Compose, PostgreSQL, Redis rate limiting, plugin loader |
| **v0.3** | Kubernetes Helm chart, CloudNativePG, mTLS (Linkerd) |

---

## Build Order (Dependency Graph)

Issues must be implemented in dependency order. Numbers show a valid topological sort.

### Phase 0: Claude Code Setup

| Issue | Title | Depends On |
|---|---|---|
| CC-001 | Create CLAUDE.md | — |
| CC-002 | Configure hooks | CC-001 |
| CC-003 | Agent team definitions | CC-001 |

### Phase 1: Solution Scaffold & Core Entities

| Issue | Title | Depends On |
|---|---|---|
| STRG-001 | Solution and project scaffold | — |
| STRG-002 | EditorConfig, .gitignore, Directory.Packages.props | STRG-001 |
| STRG-003 | TenantedEntity base classes | STRG-001 |
| STRG-004 | EF Core StrgDbContext | STRG-003 |
| STRG-005 | Initial EF Core migration | STRG-004 |

### Phase 2: Observability & API Foundation

| Issue | Title | Depends On |
|---|---|---|
| STRG-006 | Serilog structured logging | STRG-001 |
| STRG-007 | OpenTelemetry metrics + tracing | STRG-001 |
| STRG-008 | Health check endpoints | STRG-004 |
| STRG-009 | OpenAPI / Swagger configuration | STRG-001 |
| STRG-010 | Middleware pipeline order | STRG-001 |
| STRG-082 | Rate limiting middleware | STRG-010 |
| STRG-084 | HTTP security headers + HSTS | STRG-010 |
| STRG-085 | FluentValidation setup | STRG-010 |

### Phase 3: Identity & Authentication

| Issue | Title | Depends On |
|---|---|---|
| STRG-011 | User entity | STRG-003 |
| STRG-012 | OpenIddict OIDC configuration | STRG-004 |
| STRG-013 | JWT Bearer validation + auth policies | STRG-012 |
| STRG-014 | User manager (PBKDF2, lockout logic) | STRG-011 |
| STRG-015 | Password grant / token endpoint | STRG-014 |
| STRG-016 | Token refresh + revocation | STRG-015 |
| STRG-083 | Brute-force account lockout | STRG-015 |
| STRG-086 | User registration endpoint | STRG-014 |

### Phase 4: Storage Abstraction

| Issue | Title | Depends On |
|---|---|---|
| STRG-021 | IStorageProvider interface | STRG-003 |
| STRG-022 | StoragePath value type (traversal protection) | STRG-001 |
| STRG-023 | StorageProviderRegistry | STRG-021 |
| STRG-024 | LocalFileSystemProvider | STRG-021, STRG-022 |
| STRG-025 | Drive entity + REST endpoints | STRG-004 |
| STRG-026 | EncryptingStorageProvider (AES-256-GCM) | STRG-021 |
| STRG-030 | InMemoryStorageProvider (tests only) | STRG-021 |

### Phase 5: File Operations

| Issue | Title | Depends On |
|---|---|---|
| STRG-031 | FileItem + FileVersion entities + repositories | STRG-003, STRG-025 |
| STRG-032 | QuotaService (atomic SQL UPDATE) | STRG-011, STRG-004 |
| STRG-033 | FileRepository + FileVersionRepository | STRG-004, STRG-031 |
| STRG-034 | TUS upload endpoint (StrgTusStore) | STRG-031, STRG-032, STRG-024 |
| STRG-035 | Abandoned TUS upload cleanup job | STRG-034, STRG-032 |
| STRG-036 | Soft-delete purge background job | STRG-031, STRG-004 |
| STRG-037 | File download endpoint (streaming, Range) | STRG-033, STRG-025 |
| STRG-038 | File listing endpoint (pagination) | STRG-033, STRG-025 |
| STRG-039 | File delete endpoint (soft-delete) | STRG-033, STRG-061 |
| STRG-040 | File move endpoint | STRG-033, STRG-024 |
| STRG-041 | File copy endpoint | STRG-033, STRG-024 |
| STRG-042 | Folder creation endpoint (auto-parents) | STRG-033, STRG-025 |
| STRG-043 | FileVersionStore service | STRG-033, STRG-024 |
| STRG-044 | File versions endpoint (list + download) | STRG-043 |
| STRG-045 | File version restore | STRG-044 |

### Phase 6: Tags + Metadata

| Issue | Title | Depends On |
|---|---|---|
| STRG-046 | Tag entity + ITagRepository | STRG-003, STRG-031 |
| STRG-047 | TagService (upsert, remove) | STRG-046 |
| STRG-048 | Tag-based file filtering (GraphQL + REST) | STRG-046, STRG-050 |

### Phase 7: GraphQL API

| Issue | Title | Depends On |
|---|---|---|
| STRG-049 | Hot Chocolate GraphQL server setup | STRG-013 |
| STRG-050 | FileType, DriveType, file listing queries | STRG-049, STRG-025, STRG-031 |
| STRG-051 | TagType + tag mutations | STRG-049, STRG-046 |
| STRG-052 | File CRUD mutations (createFolder, deleteFile, moveFile, copyFile) | STRG-049, STRG-050 |
| STRG-053 | Drive mutations (createDrive, deleteDrive) | STRG-049, STRG-050 |
| STRG-054 | User profile queries + mutations | STRG-049, STRG-011 |
| STRG-055 | Audit log GraphQL query (admin) | STRG-049, STRG-062 |
| STRG-056 | StrgErrorFilter (exception → GraphQL error code) | STRG-049 |
| STRG-057 | Drive queries (getDrives, getDrive) | STRG-049, STRG-050 |
| STRG-058 | Query depth + complexity limits + introspection | STRG-049 |

### Phase 8: Domain Events (MassTransit Outbox)

| Issue | Title | Depends On |
|---|---|---|
| STRG-061 | Configure MassTransit with EF Outbox | STRG-004, STRG-031 |
| STRG-062 | AuditLogConsumer | STRG-061, STRG-003 |
| STRG-063 | SearchIndexConsumer (placeholder) | STRG-061 |
| STRG-064 | QuotaNotificationConsumer | STRG-061, STRG-032 |
| STRG-065 | GraphQLSubscriptionPublisher | STRG-061, STRG-049 |
| STRG-066 | FileSubscriptions GraphQL type | STRG-049, STRG-065 |

### Phase 9: WebDAV Server

| Issue | Title | Depends On |
|---|---|---|
| STRG-067 | StrgWebDavMiddleware + route registration | STRG-013, STRG-025 |
| STRG-068 | StrgWebDavStore (IWebDavStore bridge) | STRG-067, STRG-024, STRG-031 |
| STRG-069 | WebDAV PROPFIND handler | STRG-068 |
| STRG-070 | WebDAV GET + PUT handlers | STRG-068 |
| STRG-071 | WebDAV MKCOL, DELETE, COPY, MOVE | STRG-068 |
| STRG-072 | WebDAV LOCK + UNLOCK | STRG-067, STRG-068 |
| STRG-073 | WebDAV Basic Auth → JWT bridge | STRG-067, STRG-015 |
| STRG-074 | WebDAV integration tests | STRG-067 through STRG-073 |

### Phase 10: Security Hardening

| Issue | Title | Depends On |
|---|---|---|
| STRG-082 | Rate limiting (auth 10/min, global 300/min) | STRG-010 |
| STRG-083 | Account lockout (5 fails = 15min, 10 = 1hr) | STRG-014, STRG-015 |
| STRG-084 | HTTP security headers + HSTS + CORS | STRG-010 |
| STRG-085 | FluentValidation for request bodies | STRG-010 |
| STRG-086 | User self-registration endpoint | STRG-014 |

### Phase 11: Plugin System (interfaces only in v0.1)

| Issue | Title | Depends On |
|---|---|---|
| STRG-088 | Plugin contract interfaces (Strg.Plugin.Abstractions) | STRG-003 |
| STRG-089 | Plugin manifest format + validation | STRG-088 |

### Phase 12: Testing Infrastructure

| Issue | Title | Depends On |
|---|---|---|
| STRG-200 | StrgWebApplicationFactory + DatabaseFixture | STRG-001, STRG-004, STRG-030 |
| STRG-201 | Authentication integration tests | STRG-200, STRG-015, STRG-083 |
| STRG-202 | File operations integration tests | STRG-200, STRG-034 through STRG-042 |
| STRG-203 | MassTransit Outbox reliability tests | STRG-200, STRG-061, STRG-062 |

### Phase 13: Inbox Feature (v0.1 — basic pipeline)

| Issue | Title | Depends On |
|---|---|---|
| STRG-300 | Drive entity — add IsDefault field | STRG-025 |
| STRG-301 | UserInboxSettings entity | STRG-011, STRG-004 |
| STRG-302 | InboxRule domain entity + EF Core config | STRG-025, STRG-011, STRG-003, STRG-004 |
| STRG-303 | InboxRuleAction tracking entity (idempotency) | STRG-302, STRG-031, STRG-004 |
| STRG-304 | InboxRuleExecutionLog entity | STRG-302, STRG-031, STRG-004 |
| STRG-305 | FileItem — add inbox status fields | STRG-031, STRG-004 |
| STRG-306 | Inbox condition evaluators (v0.1) | STRG-302, STRG-031 |
| STRG-307 | Inbox folder auto-creation service | STRG-300, STRG-031, STRG-024, STRG-305 |
| STRG-308 | InboxProcessingConsumer | STRG-302, STRG-303, STRG-304, STRG-305, STRG-306, STRG-307, STRG-032, STRG-061 |
| STRG-309 | X-Strg-Wait-For-Rules middleware | STRG-308 |
| STRG-310 | GraphQL InboxRule CRUD mutations | STRG-302, STRG-049 |
| STRG-311 | GraphQL inboxRules + inboxFiles queries | STRG-302, STRG-304, STRG-305, STRG-049 |
| STRG-312 | GraphQL inboxFileProcessed subscription | STRG-308, STRG-049 |

---

## Issues by Claude Code Directory

```
docs/issues/
├── cc/
│   ├── CC-001-claude-md.md
│   ├── CC-002-claude-hooks.md
│   └── CC-003-agent-teams.md
└── strg/
    ├── STRG-001 through STRG-010   (scaffold, observability, middleware)
    ├── STRG-011 through STRG-016   (identity, auth, tokens)
    ├── STRG-021 through STRG-030   (storage abstraction)
    ├── STRG-031 through STRG-045   (file operations, includes STRG-035 + STRG-036 background jobs)
    ├── STRG-046 through STRG-048   (tags, metadata)
    ├── STRG-049 through STRG-058   (GraphQL API)
    ├── STRG-061 through STRG-066   (domain events, MassTransit)
    ├── STRG-067 through STRG-074   (WebDAV server)
    ├── STRG-082 through STRG-089   (security, plugin interfaces)
    ├── STRG-200 through STRG-203   (testing infrastructure)
    └── STRG-300 through STRG-328   (inbox feature: v0.1 pipeline + v0.2 advanced)
```

### Phase 14: Inbox Feature (v0.2 — advanced conditions + actions)

| Issue | Title | Depends On |
|---|---|---|
| STRG-320 | Full boolean condition tree (AND/OR/NOT) | STRG-306 |
| STRG-321 | EXIF/IPTC condition support + MetadataExtractor | STRG-306, STRG-320 |
| STRG-322 | Rename action with template engine | STRG-308 |
| STRG-323 | Tag/metadata action | STRG-308, STRG-046 |
| STRG-324 | Copy action | STRG-308, STRG-032 |
| STRG-325 | Webhook action with HMAC signing | STRG-308 |
| STRG-326 | Bulk re-run inbox rules with filter | STRG-308, STRG-311 |
| STRG-327 | simulateInboxRule dry-run mutation | STRG-306, STRG-302, STRG-320 |
| STRG-328 | System-provided rule templates | STRG-302, STRG-310 |

---

## Planned Issues (Not Yet Created)

### v0.2 Milestone

| Planned ID | Title |
|---|---|
| STRG-090 | Plugin loader with AssemblyLoadContext |
| STRG-091 | Plugin DI integration |
| STRG-093 | ISearchProvider interface |
| STRG-094 | DefaultSearchProvider (EF Core full-text) |
| STRG-095 | Search GraphQL query |
| STRG-096 | ZipStorageProvider (virtual ZIP filesystem) |
| STRG-097 | Server-side ZIP streaming endpoint |
| STRG-100 | Backup engine foundation |
| STRG-110 | ACL entry entity + service |
| STRG-111 | Share link generation |
| STRG-115 | Docker Compose deployment config |
| STRG-116 | PostgreSQL migration support |
| STRG-117 | Redis rate limiting (replace in-memory) |

### v0.3 Milestone

| Planned ID | Title |
|---|---|
| STRG-130 | Kubernetes Helm chart |
| STRG-131 | CloudNativePG operator setup |
| STRG-132 | Linkerd mTLS configuration |
| STRG-133 | Horizontal pod autoscaling |

---

## Assigned Agent Types

| Agent Type | Issues |
|---|---|
| `feature-dev:code-architect` | All STRG implementation issues |
| `feature-dev:code-reviewer` | After each phase implementation |
| `feature-dev:code-explorer` | Research tasks, understanding existing code |

---

## How to Use These Issues

1. Pick the next issue(s) with all `depends_on` issues already complete
2. Pass the issue file to the agent: `claude --issue docs/issues/strg/STRG-XXX.md`
3. After implementation, run the code reviewer agent
4. Mark the issue complete (update `status: done` in frontmatter)
5. Proceed to the next issue in dependency order
