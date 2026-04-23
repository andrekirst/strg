# strg — Claude Code Project Guide

## Project Overview

**strg** (pronounced "storage") is a self-hosted cloud storage platform written in C#/.NET 9. It replaces Microsoft OneDrive with a fully owned, extensible, API-first platform.

Repository: `github.com/andrekirst/strg`
License: Apache 2.0

---

## Architecture

```
strg/
├── src/
│   ├── Strg.Api/            ASP.NET Core host (REST + TUS upload + WebDAV)
│   ├── Strg.Core/           Domain entities, interfaces, events (NO external deps)
│   ├── Strg.Infrastructure/ EF Core, storage providers, OpenIddict, consumers
│   ├── Strg.GraphQL/        Hot Chocolate schema, types, resolvers
│   └── Strg.WebDav/         WebDAV server (NWebDav)
├── tests/
│   ├── Strg.Core.Tests/
│   ├── Strg.Api.Tests/
│   └── Strg.Integration.Tests/
└── docs/
    ├── issues/              Implementation issues (STRG-xxx, CC-xxx)
    ├── requirements/        Functional and non-functional requirements
    └── architecture/        Architecture decision records and diagrams
```

### Dependency Rules (ENFORCED — NEVER VIOLATE)

```
Strg.Core          → NO external NuGet packages (only BCL + Microsoft abstractions)
Strg.Infrastructure → depends on Strg.Core
Strg.GraphQL       → depends on Strg.Core + Strg.Infrastructure
Strg.WebDav        → depends on Strg.Core + Strg.Infrastructure
Strg.Api           → depends on all above
```

**If you are writing code in `Strg.Core`, it must have zero NuGet dependencies.**

---

## Technology Stack

| Concern | Technology |
|---|---|
| Language | C# 13 / .NET 9 |
| Database (dev) | SQLite (DataSource=:memory: for tests) |
| Database (prod) | PostgreSQL (Npgsql) |
| ORM | Entity Framework Core 9 (multi-provider) |
| Auth | OpenIddict (embedded OIDC/JWT) |
| Upload | tusdotnet (TUS resumable upload protocol) |
| GraphQL | Hot Chocolate 14 |
| Events | MassTransit + EF Core Outbox |
| WebDAV | NWebDav |
| Logging | Serilog (CompactJsonFormatter in prod) |
| Observability | OpenTelemetry → Prometheus + OTLP |

---

## Key Patterns

### Multi-Tenancy

ALL entities inherit `TenantedEntity`. EF Core global query filters enforce tenant isolation automatically. **Never call `IgnoreQueryFilters()` in application code.**

```csharp
// CORRECT: global filter handles this automatically
var files = await _db.Files.ToListAsync();

// WRONG: bypasses tenant isolation
var files = await _db.Files.IgnoreQueryFilters().ToListAsync();
```

### Soft Delete

Deleted entities have `IsDeleted = true` and `DeletedAt` set. They are excluded from all queries by the global filter. **Never use `DELETE` SQL — always soft-delete.**

### Path Safety

ALL user-supplied file paths MUST go through `StoragePath.Parse()` before reaching `IStorageProvider`. This prevents path traversal attacks.

```csharp
// CORRECT
var path = StoragePath.Parse(userInput); // throws StoragePathException if unsafe
await provider.ReadAsync(path.Value, ct);

// WRONG: never pass user input directly to storage
await provider.ReadAsync(userInput, ct);
```

### Repository Pattern

Repositories do NOT call `SaveChangesAsync`. The caller (service/endpoint handler) is responsible for committing.

```csharp
_repo.Add(newFile);
await _db.SaveChangesAsync(ct); // caller commits
```

### Outbox Events

Publish domain events AFTER `SaveChangesAsync`, never before. The outbox guarantees at-least-once delivery.

```csharp
await _db.SaveChangesAsync(ct);         // commit file + outbox record atomically
await _bus.Publish(new FileUploadedEvent(...), ct); // outbox handles actual dispatch
```

### Streaming

**Never buffer large files in memory.** Always stream file content:

```csharp
// CORRECT: streaming
var stream = await provider.ReadAsync(path, ct);
return Results.File(stream, contentType: file.MimeType, enableRangeProcessing: true);

// WRONG: loads entire file into memory
var bytes = await File.ReadAllBytesAsync(path);
return Results.File(bytes, contentType: file.MimeType);
```

---

## Security Rules

### NEVER do these:

1. **Never bypass tenant isolation** — no `IgnoreQueryFilters()` in application code.
   *Carve-out:* repositories that run pre-auth (when no JWT exists yet, so `ITenantContext.TenantId` is `Guid.Empty`) MAY use `IgnoreQueryFilters()` provided they re-apply `TenantId` and `IsDeleted` inline AND carry a justification comment. `UserRepository.GetByEmailAsync` is the canonical example — login lookup must run before auth completes.
2. **Never trust user-supplied paths** — always use `StoragePath.Parse()`
3. **Never log passwords, tokens, or secrets** — use Serilog destructuring policies
4. **Never expose `ProviderConfig`** — storage credentials live in `Drive.ProviderConfig`, which is ignored in all GraphQL types and DTOs
5. **Never expose `TenantId`** — internal field, never returned to clients
6. **Never expose stack traces in production** — `StrgErrorFilter` handles this
7. **Never store passwords in plaintext** — use `IPasswordHasher` (PBKDF2)
8. **Never use `new HttpClient()`** — always use `IHttpClientFactory`

### Auth Claims

Get user identity from JWT claims only — never trust client-provided IDs:

```csharp
// CORRECT
var userId = user.GetUserId(); // from JWT sub claim

// WRONG: client-supplied ID
var userId = request.UserId;
```

---

## Code Conventions

### C# Style (full rules in `.claude/skills/csharp-strg.md`)

- `CancellationToken` parameters are named `cancellationToken` — never `ct`, never `token`.
- Braces required for every `if`/`else`/`for`/`foreach`/`while`/`do`/`using`/`lock` body (enforced by `.editorconfig` + `EnforceCodeStyleInBuild`).
- Target framework is `net10.0` (LTS); SDK pinned in `global.json`.
- Solution file is `strg.slnx` — never reintroduce `strg.sln`.
- No magic strings for MIME types, headers, claim types, HTTP methods, or auth schemes — use `System.Net.Mime.MediaTypeNames`, `Microsoft.Net.Http.Headers.HeaderNames`, `System.Security.Claims.ClaimTypes`, `Microsoft.AspNetCore.Http.HttpMethods`, `JwtBearerDefaults`, etc. Project-specific repeating strings live in `Strg.Core/Constants/`.
- Microsoft's C# coding conventions are the baseline: https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions

### Naming

- Interfaces: `IFooService`, `IFooRepository`
- Implementations: `FooService`, `FooRepository`
- Entities: `FileItem`, `AuditEntry` (not `FileItemEntity`)
- Records: `CreateFolderRequest`, `FileItemDto`
- Events: `FileUploadedEvent`, `FileDeletedEvent`

### Class Structure

```csharp
public sealed class FooService : IFooService  // sealed by default
{
    // Private readonly fields first
    private readonly IFooRepository _repo;

    // Constructor injection
    public FooService(IFooRepository repo) => _repo = repo;

    // Public methods
    // Private methods at bottom
}
```

### Error Handling

Two patterns coexist by deliberate design — pick the one that matches the failure semantics:

**`Result` / `Result<T>`** — for *expected* failure modes that the caller will branch on. Identity and auth surfaces are the canonical example: `EmailAlreadyExists`, `InvalidPassword`, `PasswordTooShort` are not exceptional, they are part of the API contract. Use this when:
- Every failure has a stable error code the caller maps to a wire-level response.
- Throwing would force the caller into `try/catch` for normal control flow.
- The failure is not "the system is broken", it's "the input was rejected".

**Exceptions** — for *exceptional* conditions. Use this when:
- The failure is mapped centrally (`StrgErrorFilter` for GraphQL, RFC 7807 problem-details middleware for REST).
- Multiple unrelated call sites would otherwise need duplicated `if (result.IsFailure) return ...` plumbing.
- The condition is genuinely unusual (path traversal attempt, quota exceeded, soft-deleted resource lookup).

Domain exceptions in `Strg.Core/Exceptions/`:
- `StoragePathException`: invalid path
- `NotFoundException`: resource not found
- `QuotaExceededException`: quota exceeded
- `ValidationException`: input validation failed
- `DuplicateDriveNameException`: duplicate drive name

All exceptions are mapped by `StrgErrorFilter` in GraphQL and return RFC 7807 problem details in REST.

---

## Database

### Migrations

```bash
# Create a new migration
dotnet ef migrations add <MigrationName> --project src/Strg.Infrastructure --startup-project src/Strg.Api

# Apply migrations
dotnet ef database update --project src/Strg.Infrastructure --startup-project src/Strg.Api
```

### Provider switching

The database provider is selected via config:
```json
{ "Database": { "Provider": "sqlite" } }  // or "postgres"
```

---

## Running Tests

```bash
# All tests
dotnet test

# Integration tests only
dotnet test tests/Strg.Integration.Tests

# Unit tests only
dotnet test tests/Strg.Core.Tests tests/Strg.Api.Tests

# With test coverage
dotnet test --collect:"XPlat Code Coverage"
```

Integration tests use SQLite in-memory + `InMemoryStorageProvider` — no external services needed.

### Run only affected tests while iterating

During implementation, prefer targeted test runs to keep the feedback loop fast:

```bash
# Filter by fully-qualified name fragment
dotnet test --filter "FullyQualifiedName~StrgDbContextTests"

# Target a single project
dotnet test tests/Strg.Core.Tests
```

Run the full suite (`dotnet test`) only once before declaring a task complete, committing, or opening a PR.

---

## Issue Tracking

Issues are in `docs/issues/`. To implement an issue:

1. Read the issue file: `docs/issues/strg/STRG-XXX.md`
2. Implement the acceptance criteria exactly
3. Write the tests specified in "Test Cases"
4. Mark complete when all acceptance criteria pass

**Build order**: see `docs/issues/README.md` for the dependency graph.

---

## Agent Teams & Slash Commands

Pre-defined agent teams live in `.claude/agents/`. Each file describes a
multi-agent workflow: purpose, which roles to spawn, which `subagent_type`
each role maps to, expected inputs, expected outputs, and a prompt template
per role.

| Team file                            | When to use                                         |
|--------------------------------------|-----------------------------------------------------|
| `.claude/agents/feature-dev-team.md` | Implementing a new STRG-xxx or CC-xxx issue.        |
| `.claude/agents/security-team.md`    | Reviewing sensitive code or threat-modelling a new feature. |
| `.claude/agents/review-team.md`      | PR review before merge (conventions, tests, types). |

Slash commands in `.claude/commands/` drive these teams:

| Command                    | Arguments       | What it does                              |
|----------------------------|-----------------|-------------------------------------------|
| `/implement-issue <ID>`    | `STRG-xxx` or `CC-xxx` (validated with `^(STRG\|CC)-\d{3}$` — arguments that do not match are rejected before any filesystem access) | Runs the feature-dev team end-to-end on the issue. |
| `/review-pr`               | *(none)*        | Runs the review team on the current diff. |
| `/next-issue`              | *(none)*        | Finds the next unblocked issue in the dependency graph. |

### Typical workflow

1. `/next-issue` → pick the next unblocked issue.
2. `/implement-issue STRG-xxx` → feature-dev team implements it.
3. Optionally spawn the security team against the diff if the change is
   sensitive (auth, storage, paths, sharing, plugins).
4. `/review-pr` → review team checks conventions and test coverage.
5. Commit; the issue frontmatter flips to `status: done` as part of step 2.

### Subagent-type mapping

The team files document roles like `code-explorer`, `code-architect`,
`code-reviewer`, `pr-test-analyzer`, `threat-modeler`, etc. These are
*conceptual roles*, not custom `subagent_type` values. Each role maps to one
of the real Claude Code subagent types (`Explore`, `Plan`, or
`general-purpose`) — the mapping is declared at the top of every team file.
Do not pass role names as `subagent_type`; pass the mapped type and the
role-specific prompt template.

### Enforcement caveats (honest disclosure)

- The `^(STRG|CC)-\d{3}$` argument guard on `/implement-issue` is a
  **prompt-level convention**, not a harness-level sandbox. If the agent
  follows the command body, traversal arguments are rejected before any
  filesystem access; if the agent skips Step 0 it is not stopped. A
  `PreToolUse` hook in `.claude/settings.json` would be the hard
  enforcement upgrade if it is ever needed.
- Review-only roles (`impl-self-reviewer`, `code-reviewer`,
  `pr-test-analyzer`, `type-design-analyzer`, `comment-analyzer`,
  `security-reviewer`, `silent-failure-hunter`, `threat-modeler`) are
  spawned as `general-purpose`, which grants Write/Edit/Bash. Their
  read-only intent is carried by the prompt, not the harness. Treat the
  "Tool-scope intent: read-only (advisory)" label in the team files as a
  design contract that spawned agents are expected to honour.

---

## Forbidden Patterns

```csharp
// 1. Singleton DbContext (always scoped)
services.AddSingleton<StrgDbContext>(...); // FORBIDDEN

// 2. Synchronous file I/O
File.ReadAllBytes(path); // FORBIDDEN - use async always

// 3. Catching and swallowing exceptions
try { ... } catch { } // FORBIDDEN

// 4. Raw SQL with string interpolation
_db.Database.ExecuteSqlRaw($"SELECT * FROM files WHERE id = {id}"); // FORBIDDEN

// 5. Mutable entities outside the aggregate
file.TenantId = differentTenant; // FORBIDDEN - TenantId is init-only

// 6. Loading collections into memory for filtering
_db.Files.ToList().Where(f => f.DriveId == id); // FORBIDDEN - filter in LINQ/SQL

// 7. Thread.Sleep in async code
Thread.Sleep(100); // FORBIDDEN - use Task.Delay
```
