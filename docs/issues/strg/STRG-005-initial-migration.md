---
id: STRG-005
title: Create initial EF Core database migration
milestone: v0.1
priority: critical
status: done
type: implementation
labels: [infrastructure, database, migrations]
depends_on: [STRG-004, STRG-011, STRG-021, STRG-031, STRG-046]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-005: Create initial EF Core database migration

## Summary

Generate the EF Core `InitialCreate` migration that brings a fresh PostgreSQL database to the complete v0.1 schema for all entities defined at HEAD: `Tenants`, `Users`, `Drives`, `Files`, `FileVersions`, `FileKeys`, `Tags`, `AuditEntries`, `InboxRules`, plus the four OpenIddict tables (`OpenIddictApplications`, `OpenIddictScopes`, `OpenIddictAuthorizations`, `OpenIddictTokens`). PostgreSQL only — no SQLite support in production or tests.

## Background / Context

This migration is the "big bang" migration for v0.1 — it must run on a fresh database and bring it to the complete schema that the v0.1 entities expect. It was originally authored in Tranche 1 but the initial generation missed the OpenIddict tables (see "Notable prior-art gotcha" below) and predated entities that landed later in Tranches 2-4. The `InitialCreate` at HEAD is the consolidated regeneration covering everything currently in `Strg.Core/Domain/` plus OpenIddict.

### Notable prior-art gotcha

The originally-shipped initial migration was generated against a `StrgDbContextFactory` that did NOT call `options.UseOpenIddict()`. Production code DID call it (see `Program.cs`), so production expected OpenIddict tables while migrations never created them. The regeneration in this issue fixed the factory in lockstep with dropping and rebuilding the migration set — without that fix, every regeneration would reintroduce the drift.

## Technical Specification

### Migration command

```bash
dotnet ef migrations add InitialCreate \
  --project src/Strg.Infrastructure \
  --startup-project src/Strg.Api \
  --output-dir Data/Migrations
```

### Tables in the v0.1 migration at HEAD

Domain tables (one per `Strg.Core.Domain` entity currently registered on `StrgDbContext`):

- `Tenants`
- `Users` — unique index on `(TenantId, Email)`; quota columns (`QuotaBytes`, `UsedBytes`) are both `bigint`
- `Drives` — unique index on `(TenantId, Name)`; `ProviderConfig` and `VersioningPolicy` stored as `text` JSON payloads; `EncryptionEnabled` init-only boolean
- `Files` — materialized-path `Path`; unique index on `(DriveId, Path)`
- `FileVersions` — unique index on `(FileId, VersionNumber)`; `BlobSizeBytes` column (plaintext size on `Size`, ciphertext + envelope size on `BlobSizeBytes`) — see STRG-043
- `FileKeys` — `FileVersionId` unique index (one DEK per version envelope); `EncryptedDek` (bytea), `Algorithm` (varchar(32)); cascade delete from `FileVersions` — see STRG-026
- `Tags` — unique index on `(FileId, UserId, Key)`; `ValueType` varchar(10) with `CK_Tags_ValueType` check constraint — see STRG-046
- `AuditEntries`
- `InboxRules`

OpenIddict tables (managed by OpenIddict EF Core, generated via `options.UseOpenIddict()` on the design-time factory):

- `OpenIddictApplications` — unique index on `ClientId`
- `OpenIddictScopes` — unique index on `Name`
- `OpenIddictAuthorizations` — composite index on `(ApplicationId, Status, Subject, Type)`; FK to `OpenIddictApplications`
- `OpenIddictTokens` — composite index on `(ApplicationId, Status, Subject, Type)`; unique index on `ReferenceId`; FKs to `OpenIddictApplications` and `OpenIddictAuthorizations`

### Column-type invariants

- All `Guid` ids use `uuid`
- All timestamps use `timestamp with time zone`
- Binary columns use `bytea`
- String columns use `text` except where an explicit `HasMaxLength` is configured (e.g. `Tenants.Name` `varchar(64)`, `Drives.ProviderType` `varchar(50)`, `Tags.ValueType` `varchar(10)`, `FileKeys.Algorithm` `varchar(32)`)
- Soft-delete: every `TenantedEntity` has `CreatedAt` / `UpdatedAt` `timestamp with time zone NOT NULL`, `DeletedAt` `timestamp with time zone NULL` (query filter excludes deleted rows)

### Tables deferred past v0.1 (NOT in this migration)

These were listed in the earlier spec revision but the corresponding entities do not exist at HEAD. Each will require its own focused migration when the entity lands:

- `file_locks` — WebDAV `LOCK` tokens — Tranche 6 (#9)
- `file_versioning_overrides` — per-file versioning policy overrides — Tranche 7 (#10)
- `notifications` — in-app notification queue — Tranche 5 (#8)
- `acl_entries` — fine-grained ACL — Tranche 7 (#10)
- `shares` — public share links — (post-v0.1)
- MassTransit outbox tables (`OutboxMessage`, `OutboxState`, `InboxState`) — Tranche 5 (#8), requires `AddEntityFrameworkOutbox` wiring which is not yet present in `Program.cs`

The MigrationTests suite asserts the v0.1 table set exactly — when any deferred entity lands, both the migration and the test expectation update together.

## Acceptance Criteria

- [x] Migration file `20260421214650_InitialCreate.cs` lives under `src/Strg.Infrastructure/Data/Migrations/`
- [x] `dotnet ef database update` succeeds on a fresh PostgreSQL instance (verified via TestContainers in `MigrationTests.TC001`)
- [x] All v0.1 domain tables exist after `MigrateAsync` — `MigrationTests.TC001` asserts the full set
- [x] All v0.1 unique indexes exist — asserted per table in `MigrationTests.TC004/TC005`
- [x] All v0.1 FK constraints exist (FileKeys → FileVersions cascade, OpenIddict FKs)
- [x] `Tags.ValueType` check constraint rejects values outside `('string', 'number', 'boolean')` — `MigrationTests.TC006`
- [x] `MigrateAsync` is idempotent on a second run — `MigrationTests.TC002`
- [x] Design-time factory `StrgDbContextFactory` calls `options.UseOpenIddict()` so regenerations pick up OpenIddict tables

## Test Cases

Implemented in `tests/Strg.Integration.Tests/Migrations/MigrationTests.cs`. Each uses a per-test-class TestContainers Postgres and a per-test fresh database.

- **TC-001**: `MigrateAsync` on fresh Postgres creates all expected v0.1 tables (domain + OpenIddict + `__EFMigrationsHistory`)
- **TC-002**: Second `MigrateAsync` call on an already-migrated database is a no-op (idempotent)
- **TC-003**: `User` round-trip — insert with all columns set, clear change tracker, reload, all columns match
- **TC-004**: `FileKeys` has expected columns + unique index on `FileVersionId` (one DEK per version)
- **TC-005**: OpenIddict tables exist with their uniqueness invariants: `Applications.ClientId`, `Scopes.Name`, `Tokens.ReferenceId`. *(Originally specified MassTransit outbox tables; rescoped — MassTransit isn't wired until Tranche 5. The v0.1 wire-level auth tables that this migration MUST carry are OpenIddict's.)*
- **TC-006**: `CK_Tags_ValueType` rejects a raw insert with `ValueType = 'bogus'` (SqlState `23514`)

## Implementation Tasks

- [x] Ensure all v0.1 entities are defined before running migration (done as of Tranche 4 closure)
- [x] Fix `StrgDbContextFactory` to call `options.UseOpenIddict()` in lockstep with regeneration
- [x] Drop the prior `Initial` + `FileVersion_BlobSizeBytes` migrations + model snapshot
- [x] Run `dotnet ef migrations add InitialCreate`
- [x] Spot-check generated migration (all tables, indexes, FKs, check constraint)
- [x] Write `MigrationTests.cs` with TC-001..TC-006

## Security Review Checklist

- [x] Migration does not create default admin users with hardcoded passwords (first-run superadmin is provisioned at runtime by `FirstRunInitializationService`, not in the migration)
- [x] No sensitive data seeded in the migration
- [ ] `audit_entries` table has no UPDATE/DELETE permissions granted at the DB role level — deferred to the Postgres-role hardening tracker (no v0.1 issue yet; bookkept for ops hand-off). The soft-delete filter + application-layer enforcement is the v0.1 control.

## Code Review Checklist

- [x] Migration is auto-generated, not hand-written — the entire `Up`/`Down` body is EF-tooling output
- [x] Column types match entity configurations in `Strg.Infrastructure/Data/Configurations/`
- [x] All FK constraints are present (4: `FileKeys → FileVersions`, `OpenIddictAuthorizations → OpenIddictApplications`, `OpenIddictTokens → OpenIddictApplications`, `OpenIddictTokens → OpenIddictAuthorizations`)

## Definition of Done

- [x] Migration file committed
- [x] `MigrateAsync` passes on PostgreSQL (verified via MigrationTests)
- [x] All MigrationTests pass
- [x] No references to SQLite in spec or implementation for production paths (SQLite is removed — v0.1 is PostgreSQL-only)
