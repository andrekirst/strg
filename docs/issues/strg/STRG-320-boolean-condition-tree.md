---
id: STRG-320
title: Full boolean condition tree (AND/OR/NOT)
milestone: v0.2
priority: medium
status: open
type: implementation
labels: [inbox, rules, conditions]
depends_on: [STRG-306]
blocks: [STRG-321, STRG-327]
assigned_agent_type: feature-dev
estimated_complexity: small
---

# STRG-320: Full boolean condition tree (AND/OR/NOT)

## Summary

Extend `InboxConditionEvaluator` to support `Or` and `Not` logical operators in `ConditionGroup` nodes. The DB schema already supports these (the `ConditionsJson` column stores a generic tree). This issue is purely a logic change to the evaluator — no migrations needed. After this issue, the GraphQL mutation validation that rejects `OR`/`NOT` operators (added in STRG-310) must be updated to allow them.

## Technical Specification

### Evaluator change (`src/Strg.Infrastructure/Inbox/InboxConditionEvaluator.cs`)

Replace the `NotSupportedException` for `Or` and `Not`:

```csharp
private bool EvaluateGroup(ConditionGroup g, InboxEvaluationContext ctx) =>
    g.Operator switch
    {
        LogicalOperator.And => g.Children.All(c => Evaluate(c, ctx)),
        LogicalOperator.Or  => g.Children.Any(c => Evaluate(c, ctx)),
        LogicalOperator.Not => g.Children.Count == 1
            ? !Evaluate(g.Children[0], ctx)
            : throw new InvalidOperationException("NOT operator requires exactly one child."),
        _ => throw new NotSupportedException($"Unknown operator: {g.Operator}")
    };
```

### GraphQL mutation update (STRG-310 change)

Remove the v0.1 validation guard that rejected `OR`/`NOT` operators in `CreateInboxRuleInput` and `UpdateInboxRuleInput`. Add validation instead:
- `NOT` group must have exactly 1 child.
- `OR` group must have at least 2 children (warn on 1 but allow).

### UI guidance (in schema docs)

Add `[GraphQLDescription]` comments:
- `AND`: All conditions must match.
- `OR`: At least one condition must match.
- `NOT`: Inverts a single child condition.

## Acceptance Criteria

- [ ] `ConditionGroup(Or, [...])` returns `true` if any child evaluates to true
- [ ] `ConditionGroup(Not, [singleChild])` returns the inverted result
- [ ] `ConditionGroup(Not, [child1, child2])` throws `InvalidOperationException`
- [ ] Nested groups work: `Or([And([MimeType, NameGlob]), And([FileSize, DateTime])])`
- [ ] GraphQL mutation no longer rejects `OR`/`NOT` operators
- [ ] Validation added: `NOT` must have exactly 1 child

## Test Cases

- TC-001: `OR([MimeType("image/*"), MimeType("video/*")])` — true for `video/mp4`
- TC-002: `NOT([MimeType("image/*")])` — false for `image/jpeg`, true for `application/pdf`
- TC-003: `NOT([child1, child2])` — `InvalidOperationException`
- TC-004: Nested: `AND([OR([MimeType("image/*"), MimeType("video/*")]), FileSize(1024, null)])` — evaluated correctly
- TC-005: Deep nesting (5 levels) — no stack overflow; evaluates correctly

## Implementation Tasks

- [ ] Update `InboxConditionEvaluator.EvaluateGroup` to handle `Or` and `Not`
- [ ] Remove OR/NOT guard from GraphQL `CreateInboxRuleInput` validation in `InboxRuleMutations`
- [ ] Add NOT-child-count validation
- [ ] Update unit tests — add TC-001 through TC-005

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All new and existing evaluator tests pass
- [ ] No migration required
