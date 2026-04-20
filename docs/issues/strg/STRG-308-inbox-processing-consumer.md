---
id: STRG-308
title: InboxProcessingConsumer
milestone: v0.1
priority: high
status: open
type: implementation
labels: [inbox, masstransit, events, reliability]
depends_on: [STRG-302, STRG-303, STRG-304, STRG-305, STRG-306, STRG-307, STRG-032, STRG-061]
blocks: [STRG-309, STRG-311, STRG-312]
assigned_agent_type: feature-dev
estimated_complexity: large
---

# STRG-308: InboxProcessingConsumer

## Summary

Implement the core inbox processing pipeline as a MassTransit consumer of `FileUploadedEvent`. This is the most critical piece of the inbox system: it evaluates rules against the newly uploaded file, executes the first matching rule's actions (starting with Move in v0.1), and maintains idempotency via the `InboxRuleAction` tracking table. Drive-scoped rules run before user-scoped rules, both ordered by `Priority` ascending.

## Technical Specification

### New domain event (`src/Strg.Core/Events/InboxFileProcessedEvent.cs`)

```csharp
public record InboxFileProcessedEvent(
    Guid TenantId,
    Guid FileId,
    Guid UserId,
    InboxFileStatus Status,
    Guid? AppliedRuleId,
    string? NewPath    // null when no move occurred
) : IDomainEvent;
```

### Consumer (`src/Strg.Infrastructure/Inbox/InboxProcessingConsumer.cs`)

```csharp
public sealed class InboxProcessingConsumer(
    StrgDbContext db,
    IInboxConditionEvaluator evaluator,
    IQuotaService quota,
    ITopicEventSender events,
    ILogger<InboxProcessingConsumer> logger
) : IConsumer<FileUploadedEvent>
{
    public async Task Consume(ConsumeContext<FileUploadedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == msg.FileId, ct);
        if (file == null || !file.IsInInbox) return; // not an inbox file

        // Check inbox enabled
        var settings = await db.UserInboxSettings
            .FirstOrDefaultAsync(s => s.UserId == msg.UserId, ct);
        if (settings is { IsInboxEnabled: false })
        {
            await SetStatusAsync(file, InboxFileStatus.Skipped, null, ct);
            return;
        }

        // Atomic status guard: only process Pending files
        if (file.InboxStatus != InboxFileStatus.Pending) return;
        await SetStatusAsync(file, InboxFileStatus.Processing, null, ct);

        var evalCtx = new InboxEvaluationContext(file, _ => null); // v0.1: no metadata

        // Load rules: drive rules first (Priority ASC), then user rules (Priority ASC)
        var driveRules = await db.InboxRules
            .Where(r => r.DriveId == file.DriveId && r.IsEnabled)
            .OrderBy(r => r.Priority)
            .ToListAsync(ct);

        var userRules = await db.InboxRules
            .Where(r => r.UserId == msg.UserId && r.IsEnabled)
            .OrderBy(r => r.Priority)
            .ToListAsync(ct);

        InboxRule? matchedRule = null;
        foreach (var rule in driveRules.Concat(userRules))
        {
            var conditions = rule.ParseConditions();
            var matched = evaluator.Evaluate(conditions, evalCtx);

            await db.InboxRuleExecutionLogs.AddAsync(new InboxRuleExecutionLog
            {
                FileId = file.Id, RuleId = rule.Id,
                Matched = matched, TenantId = file.TenantId,
                Status = matched ? InboxRuleLogStatus.Matched : InboxRuleLogStatus.NoMatch
            }, ct);

            if (matched) { matchedRule = rule; break; } // first match wins
        }

        if (matchedRule == null)
        {
            await SetStatusAsync(file, InboxFileStatus.Skipped, null, ct);
            await db.SaveChangesAsync(ct);
            await PublishProcessedEventAsync(file, InboxFileStatus.Skipped, null, null, ct);
            return;
        }

        var actions = matchedRule.ParseActions();
        var anyFailed = false;
        string? newPath = null;

        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var success = await ExecuteActionAsync(file, matchedRule, i, action, ct, out var resultPath);
            if (resultPath != null) newPath = resultPath;
            if (!success) anyFailed = true;
        }

        var finalStatus = anyFailed ? InboxFileStatus.PartialFailure : InboxFileStatus.Processed;
        if (finalStatus == InboxFileStatus.Processed)
        {
            file.IsInInbox = false;
            file.InboxExitedAt = DateTimeOffset.UtcNow;
        }

        await SetStatusAsync(file, finalStatus, matchedRule.Id, ct);
        await db.SaveChangesAsync(ct);
        await PublishProcessedEventAsync(file, finalStatus, matchedRule.Id, newPath, ct);
    }
```

### Move action execution

```csharp
    private async Task<bool> ExecuteActionAsync(
        FileItem file, InboxRule rule, int index, InboxAction action,
        CancellationToken ct, out string? resultPath)
    {
        resultPath = null;

        // Idempotency check
        var tracking = await db.InboxRuleActions
            .FirstOrDefaultAsync(a => a.FileId == file.Id && a.RuleId == rule.Id && a.ActionIndex == index, ct);

        if (tracking?.Status == InboxRuleActionStatus.Completed)
        {
            resultPath = action is MoveAction m ? m.TargetPath : null;
            return true;
        }

        if (tracking == null)
        {
            tracking = new InboxRuleAction
            {
                FileId = file.Id, RuleId = rule.Id, ActionIndex = index,
                ActionType = GetActionType(action),
                ActionPayloadJson = JsonSerializer.Serialize(action),
                TenantId = file.TenantId
            };
            db.InboxRuleActions.Add(tracking);
            await db.SaveChangesAsync(ct); // persist plan before executing
        }

        tracking.Status = InboxRuleActionStatus.Executing;
        await db.SaveChangesAsync(ct);

        try
        {
            if (action is MoveAction move)
                resultPath = await ExecuteMoveAsync(file, move, ct);

            tracking.Status = InboxRuleActionStatus.Completed;
            tracking.ExecutedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Inbox action {ActionType} failed for file {FileId}", GetActionType(action), file.Id);
            tracking.Status = InboxRuleActionStatus.Failed;
            tracking.ErrorMessage = ex.Message[..Math.Min(ex.Message.Length, 2000)];
            await db.SaveChangesAsync(ct);
            return false;
        }
    }
```

### Move action logic

```csharp
    private async Task<string> ExecuteMoveAsync(FileItem file, MoveAction move, CancellationToken ct)
    {
        var targetDriveId = move.TargetDriveId ?? file.DriveId;
        var targetPath = StoragePath.Parse(move.TargetPath).Value; // throws StoragePathException if unsafe

        // Auto-create target folder
        if (move.AutoCreateFolders)
            await EnsurePathAsync(targetDriveId, targetPath, file.TenantId, file.CreatedBy, ct);

        // Conflict resolution
        var existing = await db.Files
            .FirstOrDefaultAsync(f => f.DriveId == targetDriveId && f.Path == targetPath + "/" + file.Name, ct);

        string finalName = file.Name;
        if (existing != null)
        {
            finalName = move.ConflictResolution switch
            {
                ConflictResolution.AutoRename => await BuildUniqueNameAsync(targetDriveId, targetPath, file.Name, ct),
                ConflictResolution.Overwrite => await SoftDeleteConflictingFileAsync(existing, ct) switch { _ => file.Name },
                ConflictResolution.Fail => throw new InvalidOperationException($"Target path conflict: {targetPath}/{file.Name}"),
                _ => file.Name
            };
        }

        var finalPath = $"{targetPath}/{finalName}".TrimStart('/');
        file.DriveId = targetDriveId;
        file.Path = "/" + finalPath;
        file.Name = finalName;

        // Quota: debit source drive owner, credit destination drive owner
        if (targetDriveId != file.DriveId)
        {
            await quota.ReleaseAsync(file.CreatedBy, file.Size, ct);
            await quota.CommitAsync(/* destination owner */ file.CreatedBy, file.Size, ct);
        }

        return file.Path;
    }
```

### MassTransit registration

```csharp
// In MassTransit config (Strg.Infrastructure/MassTransit/MassTransitConfig.cs)
cfg.AddConsumer<InboxProcessingConsumer>(c =>
{
    c.ConcurrentMessageLimit = null; // no limit — per design decision
    c.UseMessageRetry(r => r.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)));
});
```

## Acceptance Criteria

- [ ] Consumer ignores files that are not in inbox (`IsInInbox = false`)
- [ ] Consumer ignores files for users with `IsInboxEnabled = false` (sets status to Skipped)
- [ ] Idempotency: reprocessing a `Completed` action is a no-op
- [ ] Drive rules are evaluated before user rules, both ordered by `Priority ASC`
- [ ] First matching rule stops evaluation
- [ ] Execution log entry written for every rule evaluated (match and no-match)
- [ ] `InboxRuleAction` tracking rows created before each action executes
- [ ] Move action auto-creates target folders when `autoCreateFolders = true`
- [ ] Move action applies conflict resolution strategy
- [ ] Cross-drive move debits source quota and credits destination quota
- [ ] File's `IsInInbox` set to `false` and `InboxExitedAt` set on successful processing
- [ ] `InboxFileProcessedEvent` published after all actions complete
- [ ] Status transitions: Pending → Processing → Processed/PartialFailure/Failed/Skipped
- [ ] No rules matched → status = Skipped, file stays in inbox

## Test Cases

- TC-001: File not in inbox → consumer returns immediately without modifying file
- TC-002: User has `IsInboxEnabled = false` → file status set to Skipped
- TC-003: Single matching drive rule → Move action executes; file at new path; `Processed`
- TC-004: No matching rules → status = Skipped; file stays in inbox
- TC-005: Move with `AutoCreateFolders = true` → nested target folder created
- TC-006: Move with conflict, `AutoRename` strategy → file renamed to unique name
- TC-007: Move action fails (e.g., storage error) → status = PartialFailure; `InboxRuleAction` status = Failed
- TC-008: Consumer retried after crash mid-action → completed action not re-executed (idempotency)
- TC-009: Execution log has one entry per rule evaluated
- TC-010: `InboxFileProcessedEvent` published with correct `Status` and `NewPath`

## Implementation Tasks

- [ ] Create `src/Strg.Core/Events/InboxFileProcessedEvent.cs`
- [ ] Create `src/Strg.Infrastructure/Inbox/InboxProcessingConsumer.cs`
- [ ] Implement `EnsurePathAsync` helper (recursive folder creation)
- [ ] Implement `BuildUniqueNameAsync` helper (suffix _1, _2...)
- [ ] Register consumer in MassTransit config with retry policy
- [ ] Write integration tests for all TC items

## Security Review Checklist

- [ ] All target paths go through `StoragePath.Parse()` before use
- [ ] Cross-tenant rule evaluation impossible (tenant filter active in all queries)
- [ ] `IgnoreQueryFilters()` never called

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All TC-001 through TC-010 tests pass
- [ ] Consumer registered in MassTransit with retry policy
- [ ] Integration tests use in-memory storage provider (no external dependencies)
