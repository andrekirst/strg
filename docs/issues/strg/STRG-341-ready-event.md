---
id: STRG-341
title: ThumbnailReadyEvent + GraphQlSubscriptionPublisher wiring
milestone: v0.2
priority: medium
status: open
type: feature
labels: [thumbnails, phase-15, events, subscriptions, graphql]
depends_on: [STRG-331]
blocks: [STRG-340]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-341: ThumbnailReadyEvent + GraphQlSubscriptionPublisher wiring

## Summary

Add `ThumbnailReadyEvent` (already declared as part of STRG-330 deliverables) to the publish path of `ThumbnailGenerationConsumer` (STRG-331) and extend `GraphQlSubscriptionPublisher` (`src/Strg.GraphQl/Consumers/GraphQlSubscriptionPublisher.cs:10-64`) with a new `IConsumer<ThumbnailReadyEvent>` so subscribers of `thumbnailReady(fileId)` see the payload.

## Background / Context

The publisher already follows the multi-`IConsumer<T>` pattern — see the existing class which implements `IConsumer<FileUploadedEvent>`, `IConsumer<FileDeletedEvent>`, `IConsumer<FileMovedEvent>`, etc. We add one more interface implementation. No new class.

The publish-from-consumer happens BEFORE `SaveChangesAsync` so the outbox transaction stages the event atomically with the row update. This is the pattern documented in `04-event-system.md:90-108` and used everywhere in the codebase.

The subscription is **live-only** — no durable history, no replay. If the client disconnects before the event arrives, they re-fetch via the DataLoader (STRG-340) which gives them the current state.

## Technical Specification

### Event (already in STRG-330's scope) — `src/Strg.Core/Events/ThumbnailReadyEvent.cs`

```csharp
public sealed record ThumbnailReadyEvent(
    Guid TenantId, Guid FileId, Guid FileVersionId,
    string Variant, string Format, int Width, int Height) : IDomainEvent;
```

### Publisher extension — `src/Strg.GraphQl/Consumers/GraphQlSubscriptionPublisher.cs`

Existing class signature:

```csharp
public sealed class GraphQlSubscriptionPublisher(ITopicEventSender sender, ILogger<GraphQlSubscriptionPublisher> logger)
    : IConsumer<FileUploadedEvent>,
      IConsumer<FileDeletedEvent>,
      IConsumer<FileMovedEvent>,
      IConsumer<FileCopiedEvent>,
      IConsumer<FileRenamedEvent>,
      IConsumer<QuotaWarningEvent>
```

Add:

```csharp
      IConsumer<ThumbnailReadyEvent>
```

```csharp
public Task Consume(ConsumeContext<ThumbnailReadyEvent> ctx) =>
    sender.SendAsync(
        $"thumbnail-ready:{ctx.Message.FileId}",
        ctx.Message,
        ctx.CancellationToken).AsTask();
```

The topic key matches the subscriber side from STRG-340.

### Generation consumer publish — STRG-331 update

Inside `ThumbnailGenerationConsumer.ProcessAsync`, after a successful per-variant write but BEFORE the per-variant `SaveChangesAsync`:

```csharp
// Variant succeeded → row at Ready, blob written. Stage subscription event.
await bus.Publish(
    new ThumbnailReadyEvent(
        tenantId, fileId, version.Id,
        variant, "webp", result.Width, result.Height),
    cancellationToken);

await db.SaveChangesAsync(cancellationToken);   // commits row + outbox event atomically
```

Outbox semantics: the `IPublishEndpoint.Publish` call writes to the outbox table inside the same DbContext; `SaveChangesAsync` commits both. The poller dispatches the event, which the publisher then forwards to subscribers.

### Why a dedicated subscription event (not republish via direct ITopicEventSender)

The consumer cannot call `ITopicEventSender.SendAsync` directly because it's outside the GraphQL request pipeline and the topic-event abstraction is tied to the GraphQL host. Using a domain event lets MassTransit handle the cross-pipeline hop and gives us the same outbox guarantees as `FileUploadedEvent`. Idempotency: re-delivery of `ThumbnailReadyEvent` re-publishes to the topic, which subscribers handle as a no-op (same payload).

## Acceptance Criteria

- [ ] `ThumbnailReadyEvent` published to outbox before `SaveChangesAsync` for every successful variant generation.
- [ ] `GraphQlSubscriptionPublisher` consumes `ThumbnailReadyEvent` and forwards to topic `thumbnail-ready:{fileId}`.
- [ ] Topic key matches the subscriber side (STRG-340).
- [ ] Re-delivery of the event causes a duplicate topic send (acceptable — subscribers tolerate it; client-side dedup if needed).
- [ ] No new class created — the existing publisher gets one more `IConsumer<>` interface.
- [ ] Event registration in `Program.cs` MassTransit config: `busCfg.AddConsumer<GraphQlSubscriptionPublisher>()` already exists; just verify no extra registration needed.

## Test Cases

- **TC-001**: Generate a thumbnail → outbox row for `ThumbnailReadyEvent` exists in `outbox_messages` (or equivalent MassTransit table).
- **TC-002**: Subscribe to `thumbnailReady(fileId: X)`, generate a thumbnail for X → subscriber receives one payload with `status: READY` and matching `variant` / `format` / `width` / `height`.
- **TC-003**: Subscribe to `thumbnailReady(fileId: X)`, generate a thumbnail for Y → subscriber receives nothing (topic isolation per fileId).
- **TC-004**: Re-deliver the event → subscriber receives two payloads (acceptable; subscribers MUST be idempotent or tolerate dupes).

## Implementation Tasks

- [ ] Add `IConsumer<ThumbnailReadyEvent>` to `GraphQlSubscriptionPublisher` class declaration.
- [ ] Implement the `Consume(ConsumeContext<ThumbnailReadyEvent>)` method.
- [ ] Add the `await bus.Publish(new ThumbnailReadyEvent(...), ct)` call at the appropriate point in `ThumbnailGenerationConsumer.ProcessAsync` (STRG-331).
- [ ] Tests under `tests/Strg.GraphQl.Tests/Subscriptions/ThumbnailSubscriptionTests.cs` (publisher unit) + the integration test in STRG-340 (end-to-end subscription).

## Security Review Checklist

- [ ] Topic key uses `fileId` only — no `tenantId` in the topic name (the GraphQL subscription endpoint already authorizes the subscriber against the file).
- [ ] Subscription endpoint requires auth (covered by the existing GraphQL auth pipeline; verify subscription operations are NOT bypassed).
- [ ] Event payload carries `TenantId` for any future cross-pipeline routing — but the topic-publish path doesn't use it (auth happened upstream).
- [ ] No PII in the event (`Variant`, `Format`, `Width`, `Height` are all bounded server-side values).

## Code Review Checklist

- [ ] No new class — just an interface addition and a `Consume` method on the existing publisher.
- [ ] Topic-key string follows the same `{type}-{id}` convention as the existing publisher (e.g., `drive-events:{driveId}`).
- [ ] `await bus.Publish` happens BEFORE `SaveChangesAsync` (outbox).
- [ ] No magic-string topic format — extract a small helper if the publisher already has one (`Topics.FileEvents(...)` exists in the existing class).

## Definition of Done

- [ ] All acceptance criteria green.
- [ ] Tests pass.
- [ ] End-to-end smoke (with STRG-340): upload → subscribe → see payload arrive.
