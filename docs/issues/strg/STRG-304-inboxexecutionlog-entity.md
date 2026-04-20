---
id: STRG-304
title: InboxRuleExecutionLog entity
milestone: v0.1
priority: medium
status: open
type: implementation
labels: [domain, inbox, audit]
depends_on: [STRG-302, STRG-031, STRG-004]
blocks: [STRG-308, STRG-311]
assigned_agent_type: feature-dev
estimated_complexity: small
---

# STRG-304: InboxRuleExecutionLog entity

## Summary

Create `InboxRuleExecutionLog`, the permanent audit trail for inbox rule evaluation. Every rule that is evaluated against a file (whether it matched or not) produces one log entry. This table is never purged — rows accumulate permanently. Users can query their full processing history via GraphQL.

## Technical Specification

### Status enum (`src/Strg.Core/Domain/Inbox/InboxRuleLogStatus.cs`)

```csharp
public enum InboxRuleLogStatus
{
    /// <summary>Rule was evaluated and conditions matched; actions were applied.</summary>
    Matched,
    /// <summary>Rule was evaluated but conditions did not match; no actions taken.</summary>
    NoMatch,
    /// <summary>Rule matched but one or more actions failed.</summary>
    Failed,
    /// <summary>No rules evaluated (e.g., inbox disabled). File left in inbox.</summary>
    Skipped
}
```

### Domain entity (`src/Strg.Core/Domain/Inbox/InboxRuleExecutionLog.cs`)

```csharp
public sealed class InboxRuleExecutionLog : TenantedEntity
{
    public Guid FileId { get; init; }

    /// <summary>Null when status is Skipped (no rules were evaluated at all).</summary>
    public Guid? RuleId { get; init; }

    public DateTimeOffset EvaluatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Matched { get; init; }

    /// <summary>
    /// JSON snapshot of the actions that were applied (only set when Matched = true).
    /// Null when NoMatch or Skipped.
    /// </summary>
    public string? ActionsTakenJson { get; init; }

    public InboxRuleLogStatus Status { get; init; }

    /// <summary>Human-readable summary of what happened, e.g. error messages for Failed status.</summary>
    public string? Notes { get; set; }
}
```

### EF Core configuration (`src/Strg.Infrastructure/Persistence/Configurations/InboxRuleExecutionLogConfiguration.cs`)

```csharp
public class InboxRuleExecutionLogConfiguration : IEntityTypeConfiguration<InboxRuleExecutionLog>
{
    public void Configure(EntityTypeBuilder<InboxRuleExecutionLog> builder)
    {
        builder.ToTable("inbox_rule_execution_logs");

        builder.Property(l => l.ActionsTakenJson).HasColumnType("jsonb");
        builder.Property(l => l.Notes).HasMaxLength(2000);

        // Primary query patterns: history per file, history per rule
        builder.HasIndex(l => new { l.TenantId, l.FileId, l.EvaluatedAt });
        builder.HasIndex(l => new { l.TenantId, l.RuleId, l.EvaluatedAt });

        builder.HasQueryFilter(l => !l.IsDeleted && l.TenantId == /* tenantContext.TenantId */);
    }
}
```

Add `DbSet<InboxRuleExecutionLog> InboxRuleExecutionLogs => Set<InboxRuleExecutionLog>();` to `StrgDbContext`.

### Migration

New migration: `AddInboxRuleExecutionLog`.

### GraphQL surface (implemented in STRG-311)

```graphql
type InboxRuleExecutionLog {
  id: ID!
  fileId: ID!
  ruleId: ID
  evaluatedAt: DateTime!
  matched: Boolean!
  actionsTaken: [InboxActionSummary]
  status: InboxRuleLogStatus!
  notes: String
}

type Query {
  inboxRuleExecutionLogs(fileId: ID, ruleId: ID, first: Int, after: String): InboxRuleExecutionLogConnection!
}
```

## Acceptance Criteria

- [ ] `InboxRuleExecutionLog` entity exists with all specified fields
- [ ] `InboxRuleLogStatus` enum exists in `Strg.Core`
- [ ] `ActionsTakenJson` uses `jsonb` column type; is nullable
- [ ] Composite indexes on `(TenantId, FileId, EvaluatedAt)` and `(TenantId, RuleId, EvaluatedAt)`
- [ ] `RuleId` is nullable (allows Skipped-status rows with no associated rule)
- [ ] Global query filter enforces tenant isolation + soft-delete
- [ ] Migration `AddInboxRuleExecutionLog` applies cleanly

## Test Cases

- TC-001: Create a log entry with `RuleId = null` and `Status = Skipped` — persists and reads back correctly
- TC-002: Querying logs by `FileId` returns only rows for the same tenant
- TC-003: `ActionsTakenJson` round-trips a list of action summaries correctly

## Implementation Tasks

- [ ] Create `src/Strg.Core/Domain/Inbox/InboxRuleLogStatus.cs`
- [ ] Create `src/Strg.Core/Domain/Inbox/InboxRuleExecutionLog.cs`
- [ ] Create `src/Strg.Infrastructure/Persistence/Configurations/InboxRuleExecutionLogConfiguration.cs`
- [ ] Add `DbSet<InboxRuleExecutionLog>` to `StrgDbContext`
- [ ] Create EF Core migration `AddInboxRuleExecutionLog`
- [ ] Write integration tests

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All TC-001 through TC-003 tests pass
- [ ] Migration applies cleanly
