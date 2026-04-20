---
id: STRG-303
title: InboxRuleAction tracking entity (idempotency)
milestone: v0.1
priority: high
status: open
type: implementation
labels: [domain, inbox, reliability]
depends_on: [STRG-302, STRG-031, STRG-004]
blocks: [STRG-308]
assigned_agent_type: feature-dev
estimated_complexity: small
---

# STRG-303: InboxRuleAction tracking entity (idempotency)

## Summary

Create `InboxRuleAction`, a tracking table that records planned and executed actions for inbox rule processing. Before executing any file operation, the consumer writes a `InboxRuleAction` row with status `Planned`. On retry after a consumer crash, the consumer checks this table and skips already-`Completed` actions. This is the outbox-style idempotency pattern agreed in the design.

## Technical Specification

### Domain entity (`src/Strg.Core/Domain/Inbox/InboxRuleAction.cs`)

```csharp
public sealed class InboxRuleAction : TenantedEntity
{
    public Guid FileId { get; init; }
    public Guid RuleId { get; init; }

    /// <summary>Zero-based index of this action within the rule's ActionsJson array.</summary>
    public int ActionIndex { get; init; }

    /// <summary>The $type discriminator value: "move", "copy", "rename", "tag", "webhook".</summary>
    public required string ActionType { get; init; }

    /// <summary>Snapshot of the serialized InboxAction at the time of planning.</summary>
    public required string ActionPayloadJson { get; init; }

    public InboxRuleActionStatus Status { get; set; } = InboxRuleActionStatus.Planned;
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
}

public enum InboxRuleActionStatus
{
    Planned,
    Executing,
    Completed,
    Failed
}
```

### EF Core configuration (`src/Strg.Infrastructure/Persistence/Configurations/InboxRuleActionConfiguration.cs`)

```csharp
public class InboxRuleActionConfiguration : IEntityTypeConfiguration<InboxRuleAction>
{
    public void Configure(EntityTypeBuilder<InboxRuleAction> builder)
    {
        builder.ToTable("inbox_rule_actions");

        builder.Property(a => a.ActionType).HasMaxLength(50).IsRequired();
        builder.Property(a => a.ActionPayloadJson).HasColumnType("jsonb").IsRequired();
        builder.Property(a => a.ErrorMessage).HasMaxLength(2000);

        // Lookup by file + rule to check for planned/completed actions on retry
        builder.HasIndex(a => new { a.FileId, a.RuleId, a.ActionIndex }).IsUnique();
        builder.HasIndex(a => new { a.TenantId, a.FileId });

        builder.HasQueryFilter(a => !a.IsDeleted && a.TenantId == /* tenantContext.TenantId */);
    }
}
```

Add `DbSet<InboxRuleAction> InboxRuleActions => Set<InboxRuleAction>();` to `StrgDbContext`.

### Idempotency usage pattern (implemented in STRG-308)

```csharp
// Before executing action[i]:
var existing = await _db.InboxRuleActions
    .FirstOrDefaultAsync(a => a.FileId == fileId && a.RuleId == ruleId && a.ActionIndex == i, ct);

if (existing?.Status == InboxRuleActionStatus.Completed)
    continue; // already done — skip on retry

if (existing == null)
{
    _db.InboxRuleActions.Add(new InboxRuleAction
    {
        FileId = fileId, RuleId = ruleId, ActionIndex = i,
        ActionType = action.GetType().Name.ToLower().Replace("action", ""),
        ActionPayloadJson = JsonSerializer.Serialize(action),
        TenantId = tenantId
    });
    await _db.SaveChangesAsync(ct); // write plan row before executing
}

// Execute the action...
existing!.Status = InboxRuleActionStatus.Completed;
existing.ExecutedAt = DateTimeOffset.UtcNow;
await _db.SaveChangesAsync(ct);
```

### Migration

New migration: `AddInboxRuleAction`.

## Acceptance Criteria

- [ ] `InboxRuleAction` entity exists with all specified fields
- [ ] `InboxRuleActionStatus` enum exists in `Strg.Core`
- [ ] Unique index on `(FileId, RuleId, ActionIndex)` prevents duplicate tracking rows
- [ ] `ActionPayloadJson` uses `jsonb` column type
- [ ] Global query filter enforces tenant isolation + soft-delete
- [ ] Migration `AddInboxRuleAction` applies cleanly

## Test Cases

- TC-001: Creating two `InboxRuleAction` rows with the same `(FileId, RuleId, ActionIndex)` raises a unique constraint violation
- TC-002: Querying actions by `FileId` returns only rows for the same tenant
- TC-003: Soft-deleted rows excluded from default queries

## Implementation Tasks

- [ ] Create `src/Strg.Core/Domain/Inbox/InboxRuleAction.cs`
- [ ] Create `src/Strg.Infrastructure/Persistence/Configurations/InboxRuleActionConfiguration.cs`
- [ ] Add `DbSet<InboxRuleAction>` to `StrgDbContext`
- [ ] Create EF Core migration `AddInboxRuleAction`
- [ ] Write integration tests for unique constraint and tenant isolation

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All TC-001 through TC-003 tests pass
- [ ] Migration applies cleanly
