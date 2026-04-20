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

Implement the `inboxFileProcessed` GraphQL subscription that pushes a real-time notification to subscribed clients when the inbox processing pipeline finishes evaluating rules for a file. This enables UIs and CLI tools to react to rule outcomes (e.g., show the new file path, display an error badge) without polling.

## Technical Specification

### Subscription payload type (`src/Strg.GraphQL/Types/Inbox/InboxFileProcessedPayload.cs`)

```csharp
public sealed record InboxFileProcessedPayload(
    Guid FileId,
    InboxFileStatus Status,
    Guid? AppliedRuleId,
    string? NewPath,
    DateTimeOffset Timestamp
);
```

### Subscription topic constant (`src/Strg.GraphQL/Topics.cs`)

```csharp
public static class Topics
{
    public static string InboxFileProcessed(Guid tenantId) =>
        $"inbox-file-processed:{tenantId}";
}
```

Subscription is scoped per-tenant to prevent cross-tenant data leakage.

### Subscription type (`src/Strg.GraphQL/Subscriptions/InboxSubscriptions.cs`)

```csharp
[SubscriptionType]
public sealed class InboxSubscriptions
{
    [Subscribe]
    [Topic("{tenantId}")]
    public InboxFileProcessedPayload OnInboxFileProcessed(
        [EventMessage] InboxFileProcessedPayload payload) => payload;

    [SubscribeResolver]
    public ValueTask<ISourceStream<InboxFileProcessedPayload>> SubscribeToInboxFileProcessedAsync(
        [Service] ITopicEventReceiver receiver,
        [Service] ICurrentUserContext user,
        CancellationToken ct) =>
        receiver.SubscribeAsync<InboxFileProcessedPayload>(
            Topics.InboxFileProcessed(user.TenantId), ct);
}
```

### Event publishing from consumer (in STRG-308)

After all actions complete, `InboxProcessingConsumer` sends the event to the topic:

```csharp
// In InboxProcessingConsumer
await _eventSender.SendAsync(
    Topics.InboxFileProcessed(file.TenantId),
    new InboxFileProcessedPayload(
        FileId: file.Id,
        Status: finalStatus,
        AppliedRuleId: matchedRule?.Id,
        NewPath: newPath,
        Timestamp: DateTimeOffset.UtcNow),
    ct);
```

### GraphQL schema

```graphql
type Subscription {
  inboxFileProcessed: InboxFileProcessedPayload!
}

type InboxFileProcessedPayload {
  fileId: ID!
  status: InboxFileStatus!
  appliedRuleId: ID
  newPath: String
  timestamp: DateTime!
}
```

### Transport

Hot Chocolate subscriptions use WebSockets (graphql-ws protocol) or Server-Sent Events. Both are configured in STRG-049 (GraphQL base setup). No additional transport setup needed here.

## Acceptance Criteria

- [ ] `inboxFileProcessed` subscription is defined in the Hot Chocolate schema
- [ ] Subscribing requires authentication
- [ ] Subscriptions are scoped to the authenticated user's tenant (no cross-tenant events)
- [ ] `InboxProcessingConsumer` sends the event via `ITopicEventSender` after processing
- [ ] `IInboxWaitService.Notify` is also called for the wait-header feature (STRG-309)
- [ ] Payload includes `fileId`, `status`, `appliedRuleId`, `newPath`, `timestamp`

## Test Cases

- TC-001: Subscribe → upload file to inbox → receive `InboxFileProcessedPayload` with correct status
- TC-002: Subscription receives event for the uploading user's file; other tenants do not receive it
- TC-003: `newPath` is non-null when a Move action succeeded
- TC-004: `appliedRuleId` is null when no rule matched (Skipped status)

## Implementation Tasks

- [ ] Create `src/Strg.GraphQL/Types/Inbox/InboxFileProcessedPayload.cs`
- [ ] Add `Topics.InboxFileProcessed` constant to `Topics.cs`
- [ ] Create `src/Strg.GraphQL/Subscriptions/InboxSubscriptions.cs`
- [ ] Update `InboxProcessingConsumer` (STRG-308) to call `ITopicEventSender.SendAsync`
- [ ] Write integration tests using Hot Chocolate test subscription client

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All TC-001 through TC-004 tests pass
- [ ] Subscription is schema-documented with XML doc comments on the payload type
