---
id: STRG-312
title: GraphQL inboxFileProcessed subscription
milestone: v0.1
priority: medium
status: open
type: implementation
labels: [inbox, graphql, subscriptions]
depends_on: [STRG-308, STRG-049]
blocks: []
assigned_agent_type: feature-dev
estimated_complexity: small
---

# STRG-312: GraphQL inboxFileProcessed subscription

## Summary

Implement the `inboxFileProcessed` GraphQL subscription that pushes a real-time notification when the inbox processing pipeline finishes evaluating rules for a file. Scoped per-tenant to prevent cross-tenant data leakage.

## Technical Specification

### Schema:

```graphql
type Subscription {
  inboxFileProcessed: InboxFileProcessedEvent!
}

type InboxFileProcessedEvent {
  file: FileItem!       # full FileItem via DataLoader
  rulesEvaluated: Int!
  ruleMatched: InboxRule   # null if no rule matched
  actionsTaken: [String!]!
  processedAt: DateTime!
}
```

### Topic constant (already defined in `src/Strg.GraphQL/Topics.cs` from STRG-065):

```csharp
public static class Topics
{
    public static string InboxFileProcessed(Guid tenantId) =>
        $"inbox-file-processed:{tenantId}";
}
```

### Internal event DTO (`src/Strg.Core/Events/InboxProcessedEvent.cs`):

```csharp
public sealed record InboxProcessedEvent(
    Guid FileId,
    Guid TenantId,
    int RulesEvaluated,
    Guid? MatchedRuleId,
    IReadOnlyList<string> ActionsTaken,
    DateTimeOffset ProcessedAt
);
```

### File: `src/Strg.GraphQL/Subscriptions/InboxSubscriptions.cs`

```csharp
[ExtendObjectType("Subscription")]
public sealed class InboxSubscriptions
{
    [Subscribe]
    [Authorize]
    public async Task<InboxFileProcessedEvent> OnInboxFileProcessed(
        [EventMessage] InboxProcessedEvent internalEvent,
        [GlobalState("tenantId")] Guid tenantId,
        [Service] FileItemByIdDataLoader fileLoader,
        [Service] InboxRuleByIdDataLoader ruleLoader,
        CancellationToken ct)
    {
        // Tenant isolation — belt-and-suspenders over topic scoping
        if (internalEvent.TenantId != tenantId)
            throw new UnauthorizedAccessException("Subscription event tenant mismatch.");

        var file = await fileLoader.LoadAsync(internalEvent.FileId, ct);
        var matchedRule = internalEvent.MatchedRuleId.HasValue
            ? await ruleLoader.LoadAsync(internalEvent.MatchedRuleId.Value, ct)
            : null;

        return new InboxFileProcessedEvent(
            file!,
            internalEvent.RulesEvaluated,
            matchedRule,
            internalEvent.ActionsTaken,
            internalEvent.ProcessedAt);
    }

    [SubscribeResolver]
    public ValueTask<ISourceStream<InboxProcessedEvent>> SubscribeToInboxFileProcessedAsync(
        [Service] ITopicEventReceiver receiver,
        [Service] ICurrentUserContext user,
        CancellationToken ct)
        => receiver.SubscribeAsync<InboxProcessedEvent>(
               Topics.InboxFileProcessed(user.TenantId), ct);
}
```

### Event publishing from `InboxProcessingConsumer` (STRG-308):

```csharp
// After all actions complete:
await _eventSender.SendAsync(
    Topics.InboxFileProcessed(file.TenantId),
    new InboxProcessedEvent(
        FileId: file.Id,
        TenantId: file.TenantId,
        RulesEvaluated: rulesEvaluated,
        MatchedRuleId: matchedRule?.Id,
        ActionsTaken: actionSummaries,
        ProcessedAt: DateTimeOffset.UtcNow),
    ct);
```

### Output type descriptor:

```csharp
public sealed class InboxFileProcessedEventType : ObjectType<InboxFileProcessedEvent>
{
    protected override void Configure(IObjectTypeDescriptor<InboxFileProcessedEvent> descriptor)
    {
        descriptor.Field(e => e.File);
        descriptor.Field(e => e.RulesEvaluated);
        descriptor.Field(e => e.RuleMatched);
        descriptor.Field(e => e.ActionsTaken);
        descriptor.Field(e => e.ProcessedAt);
        // No TenantId on InboxFileProcessedEvent record — never exposed
    }
}
```

## Acceptance Criteria

- [ ] `inboxFileProcessed` subscription defined in schema under `Subscription` root
- [ ] Subscribing requires authentication
- [ ] Events scoped to the authenticated user's tenant (no cross-tenant events)
- [ ] Payload includes full `FileItem` object, `ruleMatched` (nullable), `actionsTaken`, `processedAt`
- [ ] `ruleMatched` is `null` when no rule matched
- [ ] `InboxProcessingConsumer` (STRG-308) calls `ITopicEventSender.SendAsync` after processing
- [ ] `Topics.InboxFileProcessed()` used in both publisher and subscriber

## Test Cases

- TC-001: Subscribe → upload file to inbox → receive `InboxFileProcessedEvent` with correct `file` and status
- TC-002: Event for tenant A's file not received by tenant B's subscriber
- TC-003: `ruleMatched` is non-null when a rule matched and acted
- TC-004: `ruleMatched` is `null` when no rule matched (Skipped status)
- TC-005: `actionsTaken` lists all actions that ran (e.g., `["move:/sorted/images"]`)

## Implementation Tasks

- [ ] Create `InboxProcessedEvent.cs` in `Strg.Core/Events/`
- [ ] Create `InboxFileProcessedEvent.cs` output record in `src/Strg.GraphQL/Payloads/`
- [ ] Create `InboxSubscriptions.cs` in `src/Strg.GraphQL/Subscriptions/`
- [ ] Create `InboxFileProcessedEventType.cs` in `src/Strg.GraphQL/Types/Inbox/`
- [ ] Update `InboxProcessingConsumer` (STRG-308) to call `ITopicEventSender.SendAsync`
- [ ] Types auto-discovered by `AddTypes()` — no manual registration

## Security Review Checklist

- [ ] `InboxFileProcessedEvent` output record has no `TenantId` field
- [ ] Tenant mismatch guard in resolver (belt-and-suspenders)
- [ ] `[Authorize]` on subscription field

## Definition of Done

- [ ] Subscription receives full event in integration test
- [ ] Tenant isolation verified
- [ ] `Topics.InboxFileProcessed()` used consistently
