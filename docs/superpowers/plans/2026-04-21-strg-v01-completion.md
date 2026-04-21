# STRG v0.1 Completion Roadmap

> **Strategy:** A-broad-features — formally close every v0.1 issue, regenerate STRG-005 migration only after all entities & schema fixes land.

**Goal:** Bring every v0.1 issue (STRG-001 through STRG-089) to `status: done` with code, tests, and verified build.

**Architecture:** Eight sequential tranches, each ending in a green build + commits. Each tranche covers a logical subsystem so progress is durable even if work pauses mid-marathon.

**Tech Stack:** C# 13 / .NET 10, EF Core 9, Npgsql, OpenIddict 7, MassTransit 8, Hot Chocolate 15, NWebDav, xUnit + FluentAssertions, Testcontainers PostgreSQL.

---

## Tranche Status

| # | Tranche | Issues | Status |
|---|---|---|---|
| 1 | Foundation finalization | STRG-004, 011, 021, 022, 031, 046 | code done, awaiting commits |
| 2 | Identity stack | STRG-012, 013, 014, 015, 016, 083, 086 | next |
| 3 | Storage abstraction | STRG-023, 024, 025, 030 | pending |
| 4 | File ops + encryption | STRG-026 (+ FileKey), 032, 033, 036, 043, 047 | pending |
| 5 | Domain events | STRG-061, 062, 063, 064 (+ Notification) | pending |
| 6 | WebDAV | STRG-067–074, 072 (+ FileLock) | pending |
| 7 | Orphaned entities + cleanup | New issues for FileVersioningOverride, AclEntry; verify Share deferred | pending |
| 8 | STRG-005 closure | Regenerate migration, MigrationTests.cs, spec amendment | pending |

## Per-tranche plans

Each tranche gets its own detailed plan saved alongside this file when work begins:

- `2026-04-21-strg-tranche-2-identity.md` — written when Tranche 2 begins
- `2026-04-21-strg-tranche-3-storage.md` — etc.

## Crosscutting principles

- **TDD for NOT_STARTED issues** (STRG-014, 024, 026, 030, 043, 047, 062, 063, 064)
- **Retroactive tests for DONE_CODE_NO_TESTS issues** (~60 issues) — read existing code, write integration tests against acceptance criteria
- **One commit per issue closure** matching repo convention `feat(strg-XXX): ...` or `test(strg-XXX): ...`
- **Run targeted tests during iteration** (`dotnet test --filter`), full suite before each commit
- **Update issue files** — mark frontmatter `status: open` → `status: done` and tick acceptance criteria checkboxes
- **No new schema until Tranche 8** — entity classes and EF configs land in Tranches 1–7; the migration captures everything at the end

## Definition of Done for the whole roadmap

- All v0.1 issues show `status: done` in their frontmatter
- All acceptance-criteria checkboxes ticked
- `dotnet test` passes (unit + integration)
- `dotnet ef database update` succeeds on fresh PostgreSQL
- New `tests/Strg.Integration.Tests/Migrations/MigrationTests.cs` passes with TC-001 through TC-006
- STRG-005's migration includes every v0.1 table including OpenIddict + MT outbox + file_keys + file_locks + notifications + file_versioning_overrides + acl_entries
