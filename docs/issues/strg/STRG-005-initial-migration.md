---
id: STRG-005
title: Create initial EF Core database migration
milestone: v0.1
priority: critical
status: open
type: implementation
labels: [infrastructure, database, migrations]
depends_on: [STRG-004, STRG-011, STRG-021, STRG-031, STRG-046]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-005: Create initial EF Core database migration

## Summary

Generate the initial EF Core migration that creates all v0.1 tables: tenants, users, drives, file_items, file_versions, file_keys, file_locks, file_versioning_overrides, notifications, tags, acl_entries, shares, audit_entries, and all OpenIddict + MassTransit outbox tables. PostgreSQL only — no SQLite fallbacks.

## Background / Context

This migration is the "big bang" migration — it must run on a fresh database and bring it to the complete v0.1 schema. It should be generated after all domain entities are defined (STRG-003, STRG-011, STRG-021, STRG-031, STRG-046 are complete).

## Technical Specification

### Migration command:

```bash
dotnet ef migrations add Initial \
  --project src/Strg.Infrastructure \
  --startup-project src/Strg.Api \
  --output-dir Data/Migrations
```

### Tables expected in migration (from `docs/requirements/05-data-model.md`):

- `tenants`
- `users` (with quota_bytes, indexes on tenant_id+email)
- `drives` (with provider_config JSONB/JSON, versioning_policy JSONB/JSON)
- `file_items` (with materialized path, composite indexes)
- `file_versions` (unique constraint on file_id+version_number)
- `file_keys` — encrypted DEK per file version (`file_version_id`, `encrypted_dek`, `algorithm`, `created_at`)
- `file_locks` — WebDAV LOCK tokens (`file_id`, `lock_token`, `owner_id`, `expires_at`, `depth`, `scope`)
- `file_versioning_overrides` — per-file versioning policy overrides (`file_id`, `policy_type`, `retain_count`, `retain_days`)
- `notifications` — in-app notifications (`user_id`, `tenant_id`, `type`, `payload_json`, `is_read`, `created_at`)
- `tags` (unique constraint on file_id+user_id+LOWER(key), indexes for tag queries)
- `acl_entries` (indexes on resource_id+type, principal)
- `shares` (unique token index)
- `audit_entries` (non-updatable, composite indexes on tenant_id+timestamp)
- OpenIddict tables (managed by OpenIddict EF Core)
- MassTransit outbox tables (managed by MassTransit EF Core)

### Migration must include:

- PostgreSQL column types: JSONB, TIMESTAMPTZ, INET, BIGINT (no SQLite fallbacks)
- All foreign key constraints
- All indexes from the data model spec
- MassTransit outbox tables via `modelBuilder.AddInboxStateEntity()`, `modelBuilder.AddOutboxMessageEntity()`, `modelBuilder.AddOutboxStateEntity()`
- `tags.value_type` column: `VARCHAR(10)` with check constraint (`'string'`, `'number'`, `'boolean'`)

## Acceptance Criteria

- [ ] Migration file generated in `src/Strg.Infrastructure/Data/Migrations/`
- [ ] `dotnet ef database update` succeeds on a PostgreSQL instance
- [ ] All tables from data model spec exist in the database
- [ ] All indexes from data model spec exist
- [ ] All unique constraints exist
- [ ] `audit_entries` table has no `UpdatedAt` trigger (append-only)
- [ ] Migration is idempotent (can run twice without error)

## Test Cases

- **TC-001**: `dotnet ef database update` on fresh PostgreSQL → all tables created
- **TC-002**: `dotnet ef database update` twice on same database → no error (idempotent)
- **TC-003**: Integration test: insert a `User`, query it, verify all columns round-trip
- **TC-004**: `file_keys`, `file_locks`, `file_versioning_overrides`, `notifications` tables exist
- **TC-005**: MassTransit outbox tables (`OutboxMessage`, `OutboxState`, `InboxState`) exist
- **TC-006**: `tags.value_type` check constraint rejects values outside ('string', 'number', 'boolean')

## Implementation Tasks

- [ ] Ensure all entities are defined before running migration (STRG-003, STRG-011, STRG-021, STRG-031, STRG-046 must be done first)
- [ ] Run `dotnet ef migrations add Initial` command
- [ ] Review generated migration — add any missing indexes manually
- [ ] Add `HasColumnType("jsonb")` for JSON/JSONB columns (PostgreSQL only, no conditionals)
- [ ] Add `HasMaxLength` constraints where appropriate
- [ ] Write migration-level integration test

## Testing Tasks

- [ ] Create `Strg.Integration.Tests/Migrations/MigrationTests.cs`
- [ ] Test migration applies cleanly to PostgreSQL (TestContainers)
- [ ] Test that all required tables exist after migration (query `pg_tables`)
- [ ] Test that indexes exist (query `pg_indexes`)
- [ ] Test that new tables `file_keys`, `file_locks`, `file_versioning_overrides`, `notifications` exist

## Security Review Checklist

- [ ] Migration does not create default admin users with hardcoded passwords
- [ ] No sensitive data seeded in the migration
- [ ] `audit_entries` has no UPDATE/DELETE permissions granted (configure at DB level)

## Code Review Checklist

- [ ] Migration is auto-generated (not hand-written) — verify it matches entity definitions exactly
- [ ] JSON column types are correct for each provider
- [ ] All FK constraints are present

## Definition of Done

- [ ] Migration file committed
- [ ] `dotnet ef database update` passes on SQLite
- [ ] Integration test passes
