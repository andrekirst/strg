---
id: STRG-305
title: FileItem — add inbox status fields
milestone: v0.1
priority: high
status: open
type: implementation
labels: [domain, inbox, storage]
depends_on: [STRG-031, STRG-004]
blocks: [STRG-307, STRG-308, STRG-311]
assigned_agent_type: feature-dev
estimated_complexity: small
---

# STRG-305: FileItem — add inbox status fields

## Summary

Extend the `FileItem` entity with four new fields that track a file's inbox lifecycle: `IsInInbox`, `InboxStatus`, `InboxEnteredAt`, and `InboxExitedAt`. These fields allow the inbox query API to filter files efficiently and provide users with a full timeline of inbox processing.

## Technical Specification

### InboxFileStatus enum (`src/Strg.Core/Domain/Inbox/InboxFileStatus.cs`)

```csharp
public enum InboxFileStatus
{
    /// <summary>File is in the inbox queue, waiting for rule evaluation to begin.</summary>
    Pending,
    /// <summary>A consumer is currently evaluating rules against this file.</summary>
    Processing,
    /// <summary>All matching rule actions completed successfully.</summary>
    Processed,
    /// <summary>The matching rule executed but at least one action failed.</summary>
    PartialFailure,
    /// <summary>The matching rule's primary action failed; file remains in inbox.</summary>
    Failed,
    /// <summary>No rules matched; file stays in inbox indefinitely until the user acts.</summary>
    Skipped
}
```

### FileItem changes (`src/Strg.Core/Domain/FileItem.cs`)

Add four properties to the existing `FileItem` entity:

```csharp
/// <summary>True while the file is physically located in the /inbox folder.</summary>
public bool IsInInbox { get; set; }

/// <summary>Current inbox processing status. Null for files that were never in inbox.</summary>
public InboxFileStatus? InboxStatus { get; set; }

/// <summary>When the file was moved into the inbox folder.</summary>
public DateTimeOffset? InboxEnteredAt { get; set; }

/// <summary>When the file was moved out of the inbox folder (rule success or manual move).</summary>
public DateTimeOffset? InboxExitedAt { get; set; }
```

### EF Core configuration (`src/Strg.Infrastructure/Persistence/Configurations/FileItemConfiguration.cs`)

Add to the existing `FileItemConfiguration`:

```csharp
builder.Property(f => f.IsInInbox).HasDefaultValue(false);
builder.Property(f => f.InboxStatus).IsRequired(false);
builder.Property(f => f.InboxEnteredAt).IsRequired(false);
builder.Property(f => f.InboxExitedAt).IsRequired(false);

// Optimized query for inbox dashboard: "show me all pending inbox files"
builder.HasIndex(f => new { f.TenantId, f.IsInInbox, f.InboxStatus })
    .HasFilter("is_in_inbox = true");
```

### Migration

New migration: `AddFileItemInboxFields` adds the four columns. All are nullable so existing rows get `null` / `false` without backfill.

### When fields are set (implemented by STRG-307 and STRG-308)

| Event | `IsInInbox` | `InboxStatus` | `InboxEnteredAt` | `InboxExitedAt` |
|---|---|---|---|---|
| File uploaded to /inbox | `true` | `Pending` | upload timestamp | null |
| Consumer picks up file | — | `Processing` | — | — |
| Rule matched, actions done | `false` | `Processed` | — | now |
| Rule matched, actions failed | `true` | `Failed` | — | null |
| No rule matched | `true` | `Skipped` | — | null |
| User manually moves file out | `false` | (unchanged) | — | now |

## Acceptance Criteria

- [ ] `InboxFileStatus` enum with 6 values exists in `Strg.Core`
- [ ] `FileItem` has `IsInInbox`, `InboxStatus`, `InboxEnteredAt`, `InboxExitedAt`
- [ ] All four new fields are nullable/default so existing rows need no migration backfill
- [ ] Partial index `(TenantId, IsInInbox, InboxStatus) WHERE is_in_inbox = true` is created
- [ ] Migration `AddFileItemInboxFields` applies cleanly
- [ ] Existing `FileItem` tests are not regressed

## Test Cases

- TC-001: New `FileItem` defaults to `IsInInbox = false`, `InboxStatus = null`
- TC-002: Updating `IsInInbox = true` and `InboxStatus = Pending` persists correctly
- TC-003: Index filters correctly — query `Files.Where(f => f.IsInInbox)` uses partial index
- TC-004: All existing `FileItem` tests pass without modification

## Implementation Tasks

- [ ] Create `src/Strg.Core/Domain/Inbox/InboxFileStatus.cs`
- [ ] Add four properties to `FileItem.cs`
- [ ] Update `FileItemConfiguration.cs` with new property mappings and partial index
- [ ] Create EF Core migration `AddFileItemInboxFields`
- [ ] Verify existing `FileItem` tests still pass

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All TC-001 through TC-004 tests pass
- [ ] Migration applies cleanly
- [ ] No existing test regressions
