---
id: STRG-065
title: Implement GraphQLSubscriptionPublisher consumer
milestone: v0.1
priority: high
status: done
type: implementation
labels: [events, graphql, subscriptions, masstransit]
depends_on: [STRG-061, STRG-049]
blocks: [STRG-066]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-065: Implement GraphQLSubscriptionPublisher consumer

## Summary

Implement `GraphQLSubscriptionPublisher` — the MassTransit consumer that bridges domain events from the outbox to Hot Chocolate's `ITopicEventSender`. This is the glue between the event system and real-time GraphQL subscriptions. It works identically regardless of which subscription backplane is active (Redis in production, in-memory in development/tests).

## Technical Specification

### Subscription backplane note

`ITopicEventSender` is an abstraction provided by Hot Chocolate. The backplane (Redis/in-memory) determines how `SendAsync` delivers events to subscribers across instances — but the consumer code is identical in both cases. The backplane is configured in STRG-049.

### Topic naming constants (`src/Strg.GraphQL/Topics.cs`):

```csharp
public static class Topics
{
    public static string FileEvents(Guid driveId) => $"file-events:{driveId}";
    public static string InboxFileProcessed(Guid tenantId) => $"inbox-file-processed:{tenantId}";
}
```

Shared between publisher and subscription type to prevent topic string mismatches.

### `FileEvent` DTO (`src/Strg.Core/Events/FileEvent.cs`):

```csharp
public enum FileEventType { Uploaded, Deleted, Moved, Copied, Renamed }

public sealed record FileEvent(
    FileEventType EventType,
    Guid FileId,
    Guid DriveId,
    Guid UserId,
    Guid TenantId,     // propagated so subscription resolver can verify tenant match
    string? OldPath,
    string? NewPath,
    DateTimeOffset OccurredAt
);
```

### File: `src/Strg.Infrastructure/Consumers/GraphQLSubscriptionPublisher.cs`

```csharp
public sealed class GraphQLSubscriptionPublisher :
    IConsumer<FileUploadedEvent>,
    IConsumer<FileDeletedEvent>,
    IConsumer<FileMovedEvent>,
    IConsumer<FileCopiedEvent>,
    IConsumer<FileRenamedEvent>
{
    private readonly ITopicEventSender _sender;
    private readonly ILogger<GraphQLSubscriptionPublisher> _logger;

    public GraphQLSubscriptionPublisher(
        ITopicEventSender sender,
        ILogger<GraphQLSubscriptionPublisher> logger)
    {
        _sender = sender;
        _logger = logger;
    }

    public Task Consume(ConsumeContext<FileUploadedEvent> context)
    {
        var msg = context.Message;
        return SendFileEvent(new FileEvent(
            FileEventType.Uploaded, msg.FileId, msg.DriveId,
            msg.UserId, msg.TenantId, null, null, DateTimeOffset.UtcNow),
            context.CancellationToken);
    }

    public Task Consume(ConsumeContext<FileDeletedEvent> context)
    {
        var msg = context.Message;
        return SendFileEvent(new FileEvent(
            FileEventType.Deleted, msg.FileId, msg.DriveId,
            msg.UserId, msg.TenantId, null, null, DateTimeOffset.UtcNow),
            context.CancellationToken);
    }

    public Task Consume(ConsumeContext<FileMovedEvent> context)
    {
        var msg = context.Message;
        return SendFileEvent(new FileEvent(
            FileEventType.Moved, msg.FileId, msg.DriveId,
            msg.UserId, msg.TenantId, msg.OldPath, msg.NewPath, DateTimeOffset.UtcNow),
            context.CancellationToken);
    }

    private async Task SendFileEvent(FileEvent fileEvent, CancellationToken ct)
    {
        await _sender.SendAsync(
            topic: Topics.FileEvents(fileEvent.DriveId),
            message: fileEvent,
            cancellationToken: ct);

        _logger.LogDebug(
            "Published {EventType} event to subscription topic {Topic}",
            fileEvent.EventType, Topics.FileEvents(fileEvent.DriveId));
    }
}
```

### Topic naming convention:

- `file-events:{driveId}` — per-drive; subscribers watch a specific drive
- `inbox-file-processed:{tenantId}` — per-tenant; inbox processing results

## Acceptance Criteria

- [ ] Consumer registered and receives all five event types (Uploaded, Deleted, Moved, Copied, Renamed)
- [ ] `ITopicEventSender.SendAsync` called with `Topics.FileEvents(driveId)`
- [ ] `FileEvent.TenantId` propagated so subscription resolver can verify tenant isolation
- [ ] Consumer does NOT read from DB (event payload provides all needed fields)
- [ ] `Topics` class used for topic naming — no raw string literals in consumer or subscription types

## Test Cases

- **TC-001**: Publish `FileUploadedEvent` → topic `file-events:{driveId}` receives `FileEvent` with `EventType = Uploaded`
- **TC-002**: Publish `FileDeletedEvent` → `EventType = Deleted`
- **TC-003**: Publish `FileMovedEvent` → `OldPath` and `NewPath` populated
- **TC-004**: Consumer disposes cleanly when `ITopicEventSender` channel is closed
- **TC-005**: `Topics.FileEvents(driveId)` matches topic string in `FileSubscriptions` (STRG-066)

## Implementation Tasks

- [ ] Create `FileEvent.cs` sealed record in `Strg.Core/Events/`
- [ ] Create `Topics.cs` in `src/Strg.GraphQL/`
- [ ] Create `GraphQLSubscriptionPublisher.cs` in `Strg.Infrastructure/Consumers/`
- [ ] Register consumer in MassTransit config (STRG-061)
- [ ] Ensure `ITopicEventSender` is available from DI (configured in STRG-049)

## Security Review Checklist

- [ ] `TenantId` included in `FileEvent` — subscription resolver verifies tenant match
- [ ] Consumer logs event type + drive ID only (no file paths in logs)
- [ ] `FileEvent.TenantId` is ignored in the GraphQL output type (never exposed to clients)

## Code Review Checklist

- [ ] Consumer is `sealed`
- [ ] Topic strings use `Topics` helper (no raw string literals)
- [ ] `FileEventType` enum matches the schema `FileEventType` enum values

## Definition of Done

- [ ] Consumer bridges all five event types to GraphQL subscription bus
- [ ] Integration test with WebSocket subscription client passes
