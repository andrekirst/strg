---
id: STRG-327
title: simulateInboxRule dry-run mutation
milestone: v0.2
priority: low
status: open
type: implementation
labels: [inbox, graphql, dry-run]
depends_on: [STRG-306, STRG-302, STRG-320]
blocks: []
assigned_agent_type: feature-dev
estimated_complexity: small
---

# STRG-327: simulateInboxRule dry-run mutation

## Summary

Implement the `simulateInboxRule` GraphQL mutation that evaluates a rule against an existing file without executing any actions. It returns whether the rule matched, a breakdown of each condition's evaluation result, and previews of what each action would have done. This is purely read-only — no state changes occur.

## Technical Specification

### GraphQL mutation and types

```graphql
type Mutation {
  simulateInboxRule(ruleId: ID!, fileId: ID!): InboxSimulationResult!
}

type InboxSimulationResult {
  matched: Boolean!
  conditionResults: [ConditionEvaluationResult!]!
  actionPreviews: [ActionPreview!]!
}

type ConditionEvaluationResult {
  conditionType: String!
  description: String!     # human-readable: "MIME type 'image/*'" etc.
  matched: Boolean!
}

type ActionPreview {
  actionType: String!
  targetPath: String        # resolved path (after conflict check, template render for RenameAction)
  targetDriveId: ID
  notes: String             # e.g. "folder '/photos/2026' would be created", "conflict: auto-rename → photo_1.jpg"
}
```

### Mutation implementation (`src/Strg.GraphQL/Mutations/InboxRuleMutations.cs`)

```csharp
public async Task<InboxSimulationResult> SimulateInboxRuleAsync(
    [ID] Guid ruleId, [ID] Guid fileId,
    [Service] StrgDbContext db,
    [Service] IInboxConditionEvaluator evaluator,
    [Service] ICurrentUserContext user,
    CancellationToken ct)
{
    var rule = await db.InboxRules.FirstOrDefaultAsync(r => r.Id == ruleId, ct)
        ?? throw new NotFoundException(nameof(InboxRule), ruleId);

    var file = await db.Files.FirstOrDefaultAsync(f => f.Id == fileId, ct)
        ?? throw new NotFoundException(nameof(FileItem), fileId);

    var conditions = rule.ParseConditions();
    var actions = rule.ParseActions();

    // Evaluate conditions with per-condition result tracking
    var conditionResults = new List<ConditionEvaluationResult>();
    var matched = EvaluateWithTrace(evaluator, conditions, new InboxEvaluationContext(file, _ => null), conditionResults);

    // Preview actions without executing
    var actionPreviews = matched
        ? actions.Select(a => PreviewAction(a, file, db)).ToList()
        : new List<ActionPreview>();

    return new InboxSimulationResult(matched, conditionResults, actionPreviews);
}
```

### Per-condition tracing

The evaluator needs a tracing mode. A simple approach: wrap each condition evaluation in a delegate that records the result:

```csharp
private bool EvaluateWithTrace(
    IInboxConditionEvaluator evaluator,
    InboxCondition root,
    InboxEvaluationContext ctx,
    List<ConditionEvaluationResult> results)
{
    // Walk the tree, record result for each leaf condition
    return TraceEvaluate(root, ctx, results, evaluator);
}

private static bool TraceEvaluate(InboxCondition c, InboxEvaluationContext ctx,
    List<ConditionEvaluationResult> results, IInboxConditionEvaluator evaluator)
{
    if (c is ConditionGroup g)
        return g.Operator == LogicalOperator.And
            ? g.Children.All(child => TraceEvaluate(child, ctx, results, evaluator))
            : g.Children.Any(child => TraceEvaluate(child, ctx, results, evaluator));

    var matched = evaluator.Evaluate(c, ctx);
    results.Add(new ConditionEvaluationResult(c.GetType().Name, Describe(c), matched));
    return matched;
}
```

### Action preview (read-only conflict check)

```csharp
private ActionPreview PreviewAction(InboxAction action, FileItem file, StrgDbContext db)
{
    return action switch
    {
        MoveAction m => new ActionPreview("move", ResolvePreviewPath(file, m, db), m.TargetDriveId?.ToString(),
            BuildMoveNotes(file, m, db)),
        CopyAction c => new ActionPreview("copy", ResolvePreviewPath(file, c, db), c.TargetDriveId?.ToString(), null),
        RenameAction r => new ActionPreview("rename", RenderTemplatePath(r, file), r.TargetDriveId?.ToString(), null),
        TagAction t => new ActionPreview("tag", null, null, $"Tags to add: {string.Join(", ", t.TagsToAdd)}"),
        WebhookAction w => new ActionPreview("webhook", null, null, $"POST to {w.Url}"),
        _ => new ActionPreview(action.GetType().Name, null, null, null)
    };
}
```

`ResolvePreviewPath` does a **read-only** conflict check (no writes), notes whether the folder exists or would be created, and whether the name would need to be auto-renamed.

## Acceptance Criteria

- [ ] `simulateInboxRule` mutation returns `matched`, `conditionResults`, `actionPreviews`
- [ ] No state changes occur (no files moved, no tags applied, no webhooks fired)
- [ ] Each leaf condition produces a `ConditionEvaluationResult` with human-readable description
- [ ] Action previews include resolved target path and notes about conflict/folder creation
- [ ] Works for rules not yet used in production (dry-run before enabling a rule)
- [ ] User must own the rule or the drive the rule belongs to

## Test Cases

- TC-001: Matching rule → `matched = true`; all condition results show correct match/no-match
- TC-002: Non-matching rule → `matched = false`; action previews are empty
- TC-003: MoveAction preview shows "folder '/photos' would be created" when folder doesn't exist
- TC-004: Conflict detected in preview → notes show "conflict: auto-rename → photo_1.jpg"
- TC-005: No side effects — querying files/rules after simulation shows no changes

## Implementation Tasks

- [ ] Add `simulateInboxRule` mutation to `InboxRuleMutations.cs`
- [ ] Add `InboxSimulationResult`, `ConditionEvaluationResult`, `ActionPreview` types
- [ ] Implement `EvaluateWithTrace` tree walker
- [ ] Implement read-only `PreviewAction` helpers
- [ ] Write integration tests verifying no state changes

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All TC-001 through TC-005 tests pass
- [ ] TC-005 verified via explicit state assertion after mutation
