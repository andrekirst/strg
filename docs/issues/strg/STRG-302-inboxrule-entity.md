---
id: STRG-302
title: InboxRule domain entity + EF Core config
milestone: v0.1
priority: high
status: open
type: implementation
labels: [domain, inbox]
depends_on: [STRG-025, STRG-011, STRG-003, STRG-004]
blocks: [STRG-303, STRG-306, STRG-308, STRG-310, STRG-311]
assigned_agent_type: feature-dev
estimated_complexity: medium
---

# STRG-302: InboxRule domain entity + EF Core config

## Summary

Define the `InboxRule` entity and all supporting domain model types. Conditions are stored as a JSON boolean tree (supporting AND/OR/NOT from day one, though v0.1 only evaluates AND nodes). Actions are stored as a JSON array of typed action definitions. This future-proof design means v0.2 condition types (EXIF, tags) and action types (copy, rename, webhook) can be added without a DB schema migration.

## Technical Specification

### Domain condition types (`src/Strg.Core/Domain/Inbox/InboxCondition.cs`)

```csharp
using System.Text.Json.Serialization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ConditionGroup), "group")]
[JsonDerivedType(typeof(MimeTypeCondition), "mimeType")]
[JsonDerivedType(typeof(NameGlobCondition), "nameGlob")]
[JsonDerivedType(typeof(FileSizeCondition), "fileSize")]
[JsonDerivedType(typeof(UploadDateTimeCondition), "uploadDateTime")]
public abstract record InboxCondition;

public record ConditionGroup(
    LogicalOperator Operator,
    IReadOnlyList<InboxCondition> Children
) : InboxCondition;

/// <summary>MIME type match: exact ("image/jpeg") or wildcard prefix ("image/*").</summary>
public record MimeTypeCondition(string MimeType) : InboxCondition;

/// <summary>File name glob match, e.g. "*.jpg", "report-??-*.pdf".</summary>
public record NameGlobCondition(string Pattern) : InboxCondition;

/// <summary>File size range in bytes. Null bound means unbounded.</summary>
public record FileSizeCondition(long? MinBytes, long? MaxBytes) : InboxCondition;

/// <summary>
/// Date/time conditions on the upload timestamp. All fields are optional;
/// specified fields are ANDed together within this condition node.
/// </summary>
public record UploadDateTimeCondition(
    DayOfWeek[]? DaysOfWeek,
    int? HourFrom,      // 0–23 inclusive
    int? HourTo,        // 0–23 inclusive
    DateTimeOffset? After,
    DateTimeOffset? Before
) : InboxCondition;

public enum LogicalOperator { And, Or, Not }
```

### Domain action types (`src/Strg.Core/Domain/Inbox/InboxAction.cs`)

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(MoveAction), "move")]
// v0.2 will add: copy, rename, tag, webhook
public abstract record InboxAction(
    ConflictResolution ConflictResolution = ConflictResolution.AutoRename,
    bool AutoCreateFolders = true
);

/// <summary>
/// Move the file to TargetPath on TargetDriveId.
/// TargetDriveId defaults to the source file's drive if null.
/// </summary>
public record MoveAction(
    string TargetPath,
    Guid? TargetDriveId = null,
    ConflictResolution ConflictResolution = ConflictResolution.AutoRename,
    bool AutoCreateFolders = true
) : InboxAction(ConflictResolution, AutoCreateFolders);

public enum ConflictResolution { AutoRename, Overwrite, Fail }
```

### InboxRule entity (`src/Strg.Core/Domain/Inbox/InboxRule.cs`)

```csharp
public sealed class InboxRule : TenantedEntity
{
    /// <summary>Null for user-scoped rules; set for drive-scoped rules.</summary>
    public Guid? DriveId { get; init; }

    /// <summary>Null for drive-scoped rules; set for user-scoped rules.</summary>
    public Guid? UserId { get; init; }

    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Lower value = higher priority. Drive rules evaluated first, then user rules, each ordered by Priority ASC.</summary>
    public int Priority { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Serialized InboxCondition tree (JSON). Root is always a ConditionGroup.</summary>
    public required string ConditionsJson { get; set; }

    /// <summary>Serialized IReadOnlyList&lt;InboxAction&gt; (JSON array).</summary>
    public required string ActionsJson { get; set; }

    // Convenience helpers — not mapped to DB columns
    public ConditionGroup ParseConditions() =>
        JsonSerializer.Deserialize<ConditionGroup>(ConditionsJson)!;

    public IReadOnlyList<InboxAction> ParseActions() =>
        JsonSerializer.Deserialize<IReadOnlyList<InboxAction>>(ActionsJson)!;
}
```

### Invariants

- Exactly one of `DriveId` or `UserId` is non-null (enforced by service, not DB).
- `ConditionsJson` must be a valid `ConditionGroup` JSON.
- `ActionsJson` must be a non-empty JSON array of `InboxAction` objects.

### EF Core configuration (`src/Strg.Infrastructure/Persistence/Configurations/InboxRuleConfiguration.cs`)

```csharp
public class InboxRuleConfiguration : IEntityTypeConfiguration<InboxRule>
{
    public void Configure(EntityTypeBuilder<InboxRule> builder)
    {
        builder.ToTable("inbox_rules");

        builder.Property(r => r.Name).HasMaxLength(200).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(1000);
        builder.Property(r => r.ConditionsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(r => r.ActionsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(r => r.Priority).HasDefaultValue(0);
        builder.Property(r => r.IsEnabled).HasDefaultValue(true);

        builder.HasIndex(r => new { r.TenantId, r.UserId, r.Priority });
        builder.HasIndex(r => new { r.TenantId, r.DriveId, r.Priority });

        builder.HasQueryFilter(r => !r.IsDeleted && r.TenantId == /* tenantContext.TenantId */);

        builder.Ignore(r => r.ParseConditions);  // helper methods are not mapped
        builder.Ignore(r => r.ParseActions);
    }
}
```

Add `DbSet<InboxRule> InboxRules => Set<InboxRule>();` to `StrgDbContext`.

### Migration

New migration: `AddInboxRule` that creates the `inbox_rules` table with `jsonb` columns.

## Acceptance Criteria

- [ ] `InboxCondition` sealed hierarchy with `[JsonPolymorphic]` attributes exists in `Strg.Core`
- [ ] `InboxAction` sealed hierarchy with `[JsonPolymorphic]` attributes exists in `Strg.Core`
- [ ] `ConflictResolution` and `LogicalOperator` enums exist in `Strg.Core`
- [ ] `InboxRule` entity with `ConditionsJson`, `ActionsJson`, `Priority`, `IsEnabled`, `DriveId`, `UserId`
- [ ] `ParseConditions()` and `ParseActions()` round-trip without data loss for all v0.1 types
- [ ] EF Core configuration uses `jsonb` column type for Postgres
- [ ] Composite indexes on `(TenantId, UserId, Priority)` and `(TenantId, DriveId, Priority)`
- [ ] Global query filter enforces tenant isolation and soft-delete
- [ ] Migration `AddInboxRule` applies cleanly

## Test Cases

- TC-001: Serialize/deserialize `ConditionGroup(And, [MimeTypeCondition("image/*"), NameGlobCondition("*.jpg")])` round-trips correctly
- TC-002: Serialize/deserialize `MoveAction("/photos/2026", null, AutoRename, true)` round-trips correctly
- TC-003: Unknown `$type` in JSON throws `JsonException` (no silent data loss)
- TC-004: EF Core can save and retrieve an `InboxRule` with both JSON columns populated
- TC-005: Global query filter excludes rules from a different tenant
- TC-006: Soft-deleted rules excluded from queries

## Implementation Tasks

- [ ] Create `src/Strg.Core/Domain/Inbox/` directory
- [ ] Create `InboxCondition.cs` with all record types and `LogicalOperator` enum
- [ ] Create `InboxAction.cs` with `MoveAction` and `ConflictResolution` enum
- [ ] Create `InboxRule.cs` entity
- [ ] Create `InboxRuleConfiguration.cs` in `Strg.Infrastructure`
- [ ] Add `DbSet<InboxRule>` to `StrgDbContext`
- [ ] Create EF Core migration `AddInboxRule`
- [ ] Write serialization unit tests for condition/action round-trips
- [ ] Write EF Core integration tests

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All TC-001 through TC-006 tests pass
- [ ] Migration applies cleanly on a fresh database
- [ ] No external NuGet packages added to `Strg.Core` (uses only `System.Text.Json` from BCL)
