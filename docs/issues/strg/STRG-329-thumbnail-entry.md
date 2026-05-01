---
id: STRG-329
title: ThumbnailEntry entity + EF config + migration + repository
milestone: v0.2
priority: high
status: open
type: feature
labels: [thumbnails, phase-15, storage, ef-core]
depends_on: [STRG-031, STRG-061]
blocks: [STRG-330, STRG-331, STRG-332, STRG-339, STRG-340, STRG-342]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-329: ThumbnailEntry entity + EF config + migration + repository

## Summary

Introduce the `ThumbnailEntry` aggregate that tracks every thumbnail blob produced for a `FileVersion`. Bundles the entity, EF Core configuration with a pinned unique-index name, the migration, the constraint-name constants class, and the repository — mirroring the STRG-031 entity-bundle precedent.

## Background / Context

Phase 15 introduces image thumbnail generation as a net-new subsystem (see issue #52 for the full tranche proposal). The consumer (STRG-331) needs a row to write per `(FileVersion, Variant, Format)` triple, with an idempotency key it can rely on under at-least-once redelivery. Decision **D3** in the tranche selected a dedicated `ThumbnailEntry` table over fields-on-`FileVersion` or cache-only — variants × formats × encrypted-drive-carve-out rows make a relational table the only sane shape.

## Technical Specification

### Entity — `src/Strg.Core/Domain/ThumbnailEntry.cs`

```csharp
public sealed class ThumbnailEntry : TenantedEntity
{
    public required Guid FileVersionId { get; init; }
    public required string Variant { get; init; }       // "thumb" | "small" | "medium"
    public required string Format { get; init; }        // "webp" | "jpeg"
    public string StorageKey { get; set; } = "";        // empty until Status=Ready
    public long SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public ThumbnailStatus Status { get; set; } = ThumbnailStatus.Pending;
    public string? ErrorReason { get; set; }            // max 256
    public DateTimeOffset? GeneratedAt { get; set; }
    public required string GeneratorVersion { get; init; }  // future re-gen trigger
}
```

`ThumbnailStatus` lives at `src/Strg.Core/Domain/Thumbnails/ThumbnailStatus.cs`:

```csharp
public enum ThumbnailStatus { Pending, Ready, Failed, Unsupported }
```

### Pinned constraint name — `src/Strg.Core/Constants/ThumbnailConstraintNames.cs`

```csharp
public static class ThumbnailConstraintNames
{
    // Authoritative name of the unique index on (FileVersionId, Variant, Format).
    // Three consumers depend on this string: ThumbnailEntryConfiguration (EF pin via
    // HasDatabaseName), ThumbnailGenerationConsumer.IsThumbnailUniqueViolation
    // (equality-match on Npgsql PostgresException.ConstraintName), and MigrationTests
    // (schema pin). Mirrors the AuditEntryConstraintNames precedent exactly.
    public const string UniqueIndex = "IX_ThumbnailEntries_FileVersionId_Variant_Format";
}
```

### EF configuration — `src/Strg.Infrastructure/Data/Configurations/ThumbnailEntryConfiguration.cs`

- Table name `ThumbnailEntries`.
- `Variant` and `Format` as `varchar(16)`; `ErrorReason` as `varchar(256)` nullable; `StorageKey` as `varchar(512)`; `GeneratorVersion` as `varchar(32)`.
- Unique index on `(FileVersionId, Variant, Format)` with `HasDatabaseName(ThumbnailConstraintNames.UniqueIndex)`.
- Foreign key to `FileVersion` with `OnDelete(DeleteBehavior.Cascade)` so version-row deletion wipes thumbnail rows; blob cleanup is explicit (STRG-332).
- Index on `Status` to support backfill `WHERE Status NOT IN (Ready, Unsupported)` enumeration without a sequential scan.
- `Status` mapped via `HasConversion<string>()` for postgres-side readability and easy migration.
- Soft-delete via the inherited global query filter (`TenantedEntity` base).

### Migration — `src/Strg.Infrastructure/Data/Migrations/YYYYMMDDhhmmss_AddThumbnails.cs`

Generated via `dotnet ef migrations add AddThumbnails --project src/Strg.Infrastructure --startup-project src/Strg.Api`. Manually verify the migration honours the `IX_ThumbnailEntries_FileVersionId_Variant_Format` name pin — EF emits it from `HasDatabaseName`, but the name is pinned so a future rename surfaces as a compile break in the constants class.

### Repository — `src/Strg.Infrastructure/Thumbnails/ThumbnailRepository.cs`

```csharp
public interface IThumbnailRepository
{
    void Add(ThumbnailEntry entry);
    Task<IReadOnlyList<ThumbnailEntry>> GetByFileVersionAsync(Guid fileVersionId, CancellationToken cancellationToken);
    Task<ThumbnailEntry?> GetAsync(Guid fileVersionId, string variant, string format, CancellationToken cancellationToken);
    Task<IReadOnlyList<ThumbnailEntry>> GetByFileAsync(Guid fileId, CancellationToken cancellationToken);
    void SoftDeleteRange(IEnumerable<ThumbnailEntry> entries);
}
```

Per project convention, the repository does NOT call `SaveChangesAsync` — the caller (consumer / endpoint handler) commits.

### DbContext registration — `src/Strg.Infrastructure/Data/StrgDbContext.cs`

Add `public DbSet<ThumbnailEntry> ThumbnailEntries => Set<ThumbnailEntry>();` and `modelBuilder.ApplyConfiguration(new ThumbnailEntryConfiguration());`.

## Acceptance Criteria

- [ ] `ThumbnailEntry` extends `TenantedEntity` with the fields above.
- [ ] `ThumbnailStatus` enum lives in `Strg.Core` (no NuGet deps introduced).
- [ ] `ThumbnailConstraintNames.UniqueIndex` is the single source of truth for the index name.
- [ ] EF configuration pins the index name via `HasDatabaseName(ThumbnailConstraintNames.UniqueIndex)`.
- [ ] Migration generated and applied to the test database; PostgreSQL + SQLite both build.
- [ ] Cascade from `FileVersion` deletion to `ThumbnailEntry` rows is in place at the DB level.
- [ ] `IThumbnailRepository` does not call `SaveChangesAsync`.
- [ ] Architecture test (`Strg.Architecture.Tests`) confirms `Strg.Core` still has zero NuGet deps after the new files land.

## Test Cases

- **TC-001**: Inserting two `ThumbnailEntry` rows with the same `(FileVersionId, Variant, Format)` triggers `DbUpdateException` whose inner `PostgresException.SqlState == "23505"` and `ConstraintName == ThumbnailConstraintNames.UniqueIndex`.
- **TC-002**: `MigrationTests.SchemaMatchesModel()` passes after the new migration is added (no model-drift warning).
- **TC-003**: Soft-deleting the `FileVersion` cascades to its `ThumbnailEntry` rows (DB-level cascade trigger, verified via integration test with EF query post-delete).
- **TC-004**: `IThumbnailRepository.GetByFileVersionAsync` honours the inherited tenant filter — a row with a different `TenantId` is invisible.

## Implementation Tasks

- [ ] Add `ThumbnailEntry`, `ThumbnailStatus`, `ThumbnailConstraintNames` to `Strg.Core`.
- [ ] Add `ThumbnailEntryConfiguration` and apply in `StrgDbContext.OnModelCreating`.
- [ ] Add `DbSet<ThumbnailEntry>` to `StrgDbContext`.
- [ ] Generate migration `AddThumbnails`; verify constraint name in the generated SQL.
- [ ] Add `IThumbnailRepository` (Core) + `ThumbnailRepository` (Infrastructure).
- [ ] Update `MigrationTests` (or equivalent) to cover the new schema.
- [ ] Architecture test sanity: run `dotnet test tests/Strg.Architecture.Tests`.

## Security Review Checklist

- [ ] `ThumbnailEntry` inherits the tenant filter (no `IgnoreQueryFilters` anywhere in this PR).
- [ ] `ErrorReason` is bounded at 256 chars to avoid log/DB amplification on adversarial inputs.
- [ ] `StorageKey` is treated as opaque — never concatenated into paths without `StoragePath.Parse()` (enforced in STRG-330's key builder).
- [ ] No PII in any field; `GeneratorVersion` is a static identifier, not user input.

## Code Review Checklist

- [ ] No `SaveChangesAsync` calls in the repository.
- [ ] Constraint name centralized in the constants class — not duplicated as a magic string anywhere.
- [ ] `Status` HasConversion<string>() in EF config (postgres readability).
- [ ] Migration tested against both `Database:Provider = sqlite` and `postgres`.
- [ ] No `Cascade` left on accidental aggregate roots; only `FileVersion → ThumbnailEntry`.

## Definition of Done

- [ ] All acceptance criteria green with concrete evidence (`file:line` or test method).
- [ ] Migration applied cleanly on a fresh DB and on the integration test fixture.
- [ ] Architecture tests pass.
- [ ] No NuGet packages added to `Strg.Core`.
