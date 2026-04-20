---
id: STRG-065
title: Implement GraphQLSubscriptionPublisher consumer
milestone: v0.1
priority: high
status: open
type: implementation
labels: [events, graphql, subscriptions, masstransit]
depends_on: [STRG-061, STRG-049]
blocks: [STRG-066]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-065: Implement GraphQLSubscriptionPublisher consumer

## Summary

Implement the `GraphQLSubscriptionPublisher` that bridges MassTransit outbox events to Hot Chocolate's in-memory subscription bus. This is the glue between the event system and real-time GraphQL subscriptions.

## Technical Specification

### File: `src/Strg.Infrastructure/Consumers/GraphQLSubscriptionPublisher.cs`

```csharp
public sealed class GraphQLSubscriptionPublisher :
    IConsumer<FileUploadedEvent>,
    IConsumer<FileDeletedEvent>,
    IConsumer<FileMovedEvent>
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

    public async Task Consume(ConsumeContext<FileUploadedEvent> context)
    {
        var msg = context.Message;
        var fileEvent = new FileEvent
        {
            Type = FileEventType.Uploaded,
            FileId = msg.FileId,
            DriveId = msg.DriveId,
            UserId = msg.UserId,
            TenantId = msg.TenantId,
            OccurredAt = DateTimeOffset.UtcNow
        };

        await _sender.SendAsync(
            topic: $"file-events:{msg.DriveId}",
            message: fileEvent,
            context.CancellationToken);

        _logger.LogDebug("Published FileEvent to subscription topic file-events:{DriveId}", msg.DriveId);
    }

    public async Task Consume(ConsumeContext<FileDeletedEvent> context)
    {
        var msg = context.Message;
        await _sender.SendAsync(
            topic: $"file-events:{msg.DriveId}",
            message: new FileEvent
            {
                Type = FileEventType.Deleted,
                FileId = msg.FileId,
                DriveId = msg.DriveId,
                UserId = msg.UserId,
                TenantId = msg.TenantId,
                OccurredAt = DateTimeOffset.UtcNow
            },
            context.CancellationToken);
    }

    public async Task Consume(ConsumeContext<FileMovedEvent> context)
    {
        var msg = context.Message;
        await _sender.SendAsync(
            topic: $"file-events:{msg.DriveId}",
            message: new FileEvent
            {
                Type = FileEventType.Moved,
                FileId = msg.FileId,
                DriveId = msg.DriveId,
                UserId = msg.UserId,
                TenantId = msg.TenantId,
                OldPath = msg.OldPath,
                NewPath = msg.NewPath,
                OccurredAt = DateTimeOffset.UtcNow
            },
            context.CancellationToken);
    }
}
```

### `FileEvent` DTO (in `Strg.Core/Events/FileEvent.cs`):

```csharp
public enum FileEventType { Uploaded, Deleted, Moved }

public class FileEvent
{
    public FileEventType Type { get; init; }
    public Guid FileId { get; init; }
    public Guid DriveId { get; init; }
    public Guid UserId { get; init; }
    public Guid TenantId { get; init; }
    public string? OldPath { get; init; }
    public string? NewPath { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
}
```

### Topic naming convention:

- Per-drive: `file-events:{driveId}` — subscribers watching a specific drive's events
- Subscribers filter tenant via `[Authorize]` in the subscription type (STRG-066)

## Acceptance Criteria

- [ ] Consumer registered and receives all three event types
- [ ] `ITopicEventSender.SendAsync` called with topic `file-events:{driveId}`
- [ ] `FileEvent.TenantId` propagated so subscription resolver can enforce tenant isolation
- [ ] Consumer does NOT read from DB (event payload provides all needed fields)
- [ ] `ITopicEventSender` resolved from DI (registered by `AddInMemorySubscriptions()`)

## Test Cases

- **TC-001**: Publish `FileUploadedEvent` → subscription topic `file-events:{driveId}` receives `FileEvent` with type `Uploaded`
- **TC-002**: Publish `FileDeletedEvent` → topic receives `FileEvent` with type `Deleted`
- **TC-003**: Publish `FileMovedEvent` → `FileEvent.OldPath` and `NewPath` populated
- **TC-004**: Consumer disposes cleanly when `ITopicEventSender` channel is closed

## Implementation Tasks

- [ ] Create `FileEvent.cs` and `FileEventType` in `Strg.Core/Events/`
- [ ] Create `GraphQLSubscriptionPublisher.cs` in `Strg.Infrastructure/Consumers/`
- [ ] Register consumer in MassTransit config (STRG-061)
- [ ] Ensure `ITopicEventSender` is available from DI (`AddInMemorySubscriptions()` in STRG-049)

## Testing Tasks

- [ ] Integration test: publish domain event → Hot Chocolate subscription receives `FileEvent`
- [ ] Verify topic string format (`file-events:{driveId}`) matches subscription type (STRG-066)

## Security Review Checklist

- [ ] `TenantId` included in `FileEvent` — subscription resolver must verify tenant match
- [ ] Topic includes `driveId` but not `tenantId` — isolation enforced at subscription level
- [ ] Consumer does not log file paths (only IDs)

## Code Review Checklist

- [ ] Consumer is `sealed`
- [ ] Topic string is a constant or helper method to avoid typos
- [ ] Three event types share same topic (correct — subscribers watch a drive, not an event type)

## Definition of Done

- [ ] Consumer bridges all three event types to GraphQL subscription bus
- [ ] Integration test with WebSocket subscription client passes
