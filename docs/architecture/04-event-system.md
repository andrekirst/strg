# Event System (Outbox Pattern)

## Design

strg uses the **Transactional Outbox Pattern** for reliable asynchronous processing.

When a file operation completes, the event is written to the `outbox_events` table in the **same database transaction** as the data change. A background poller reads unprocessed events and dispatches them to registered handlers.

This guarantees:
- **No lost events**: if the process crashes after the file is created but before the event is dispatched, the event will be picked up on restart
- **No orphan events**: if the DB transaction rolls back, the event is also rolled back — no ghost events
- **At-least-once delivery**: events may be delivered more than once; handlers must be idempotent

---

## Implementation

MassTransit's built-in Outbox is used for event storage and dispatch:

```csharp
services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<StrgDbContext>(o =>
    {
        o.UsePostgres();     // or UseSqlite() for dev
        o.UseBusOutbox();
    });

    x.AddConsumer<FileUploadedHandler>();
    x.AddConsumer<FileDeletedHandler>();
    x.AddConsumer<BackupTriggerHandler>();
    x.AddConsumer<SearchIndexHandler>();
    x.AddConsumer<AuditLogHandler>();
    x.AddConsumer<QuotaNotificationHandler>();

    x.UsingInMemory((ctx, cfg) =>
    {
        cfg.ConfigureEndpoints(ctx);
    });
});
```

The in-memory transport is used in v0.1. Switching to RabbitMQ, NATS, or Azure Service Bus in v0.3 requires only changing the transport configuration — all consumers and producers remain identical.

---

## Event Types

```csharp
// All events implement IStrgEvent
public record FileUploadedEvent(Guid FileId, Guid DriveId, Guid UserId, long Size, string MimeType);
public record FileDeletedEvent(Guid FileId, Guid DriveId, Guid UserId);
public record FileMovedEvent(Guid FileId, Guid DriveId, string OldPath, string NewPath);
public record FileSharedEvent(Guid FileId, Guid SharedByUserId, Guid? SharedWithUserId, string? ShareToken);
public record BackupStartedEvent(Guid DriveId, string BackupType);
public record BackupCompletedEvent(Guid DriveId, string BackupType, long BytesWritten, TimeSpan Duration);
public record QuotaWarningEvent(Guid UserId, long UsedBytes, long QuotaBytes);
```

---

## Event Handlers

| Event | Handler | Action |
|-------|---------|--------|
| `FileUploadedEvent` | `SearchIndexHandler` | Index file in active search provider |
| `FileUploadedEvent` | `AiTaggerHandler` | Request tag suggestions (if AI plugin installed) |
| `FileUploadedEvent` | `ThumbnailHandler` | Generate thumbnail (if plugin installed) |
| `FileUploadedEvent` | `QuotaNotificationHandler` | Check if user exceeded 80% quota |
| `FileDeletedEvent` | `SearchIndexHandler` | Remove from search index |
| `FileDeletedEvent` | `AclCleanupHandler` | Remove dangling ACL entries |
| `BackupCompletedEvent` | `AuditLogHandler` | Write backup audit entry |
| `*` | `GraphQLSubscriptionPublisher` | Push event to subscribed GraphQL clients |

All handlers are idempotent — safe to call multiple times for the same event.

---

## Publishing Events

```csharp
public class FileService(IPublishEndpoint bus, StrgDbContext db)
{
    public async Task<FileItem> CompleteUploadAsync(Guid uploadId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var file = new FileItem { /* ... */ };
        db.Files.Add(file);

        // Event written to outbox_events in the same transaction
        await bus.Publish(new FileUploadedEvent(file.Id, file.DriveId, ...), ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return file;
    }
}
```

---

## Outbox Polling

The MassTransit Outbox poller runs as a `BackgroundService` inside the API process. It:
1. Polls `outbox_events` every 100ms for unprocessed events
2. Dispatches events to in-memory handlers
3. Marks events as `processed_at = NOW()` on success
4. On failure: increments `attempt_count`, sets `error`, retries with exponential backoff
5. Events with `attempt_count >= 10` are moved to a dead-letter table for manual inspection

---

## GraphQL Subscription Bridge

GraphQL subscriptions are powered by the same event system. The `GraphQLSubscriptionPublisher` handler converts domain events to subscription payloads:

```csharp
public class GraphQLSubscriptionPublisher(ITopicEventSender sender)
    : IConsumer<FileUploadedEvent>
{
    public async Task Consume(ConsumeContext<FileUploadedEvent> ctx)
    {
        await sender.SendAsync(
            $"drive-events:{ctx.Message.DriveId}",
            new FileEventPayload { Type = "file.uploaded", FileId = ctx.Message.FileId }
        );
    }
}
```

Clients subscribe per drive:
```graphql
subscription {
  fileEvents(driveId: "...") {
    type
    file { id name size }
  }
}
```
