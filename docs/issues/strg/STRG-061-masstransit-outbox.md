---
id: STRG-061
title: Configure MassTransit Outbox with domain events
milestone: v0.1
priority: critical
status: open
type: implementation
labels: [infrastructure, events, masstransit]
depends_on: [STRG-004, STRG-031]
blocks: [STRG-062, STRG-063, STRG-064, STRG-065, STRG-066]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-061: Configure MassTransit Outbox with domain events

## Summary

Configure MassTransit with the Entity Framework Outbox pattern. Events are written atomically to the `outbox_events` table, and background polling dispatches them to in-process consumers.

## Technical Specification

### Packages: `MassTransit`, `MassTransit.EntityFrameworkCore`, `MassTransit.RabbitMQ`

### Transport: **RabbitMQ** (from v0.1). RabbitMQ connection configured in `appsettings.json`:
```json
{
  "RabbitMQ": { "Host": "localhost", "VirtualHost": "/", "Username": "guest", "Password": "guest" }
}
```

### Registration in `Program.cs`:

```csharp
builder.Services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<StrgDbContext>(o =>
    {
        o.UsePostgres();  // PostgreSQL only — no SQLite branch
        o.UseBusOutbox();
        // Polling interval: configurable, default 5 seconds
        o.QueryDelay = TimeSpan.FromSeconds(
            config.GetValue("MassTransit:OutboxPollingIntervalSeconds", 5));
    });

    x.AddConsumer<AuditLogConsumer>();
    x.AddConsumer<QuotaNotificationConsumer>();
    x.AddConsumer<GraphQLSubscriptionPublisher>();
    x.AddConsumer<SearchIndexConsumer>();

    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(config["RabbitMQ:Host"], config["RabbitMQ:VirtualHost"], h =>
        {
            h.Username(config["RabbitMQ:Username"]);
            h.Password(config["RabbitMQ:Password"]);
        });
        cfg.ConfigureEndpoints(ctx);
    });
});
```

### Domain events to define in `Strg.Core/Events/`:

```csharp
public record FileUploadedEvent(Guid TenantId, Guid FileId, Guid DriveId, Guid UserId, long Size, string MimeType);
public record FileDeletedEvent(Guid TenantId, Guid FileId, Guid DriveId, Guid UserId);
public record FileMovedEvent(Guid TenantId, Guid FileId, Guid DriveId, string OldPath, string NewPath, Guid UserId);
public record BackupCompletedEvent(Guid TenantId, Guid DriveId, long BytesWritten, TimeSpan Duration);
public record QuotaWarningEvent(Guid TenantId, Guid UserId, long UsedBytes, long QuotaBytes);
```

## Acceptance Criteria

- [ ] Publishing a `FileUploadedEvent` in the same DB transaction as file creation → event committed atomically
- [ ] Process crash between event write and dispatch → event redelivered on restart
- [ ] Events consumed exactly once for each handler (via idempotency key or exactly-once processing)
- [ ] RabbitMQ transport used from v0.1
- [ ] `MassTransit.EntityFrameworkCore` outbox tables created in migration (`InboxState`, `OutboxMessage`, `OutboxState`)
- [ ] Outbox polling interval: 5 seconds default, configurable via `"MassTransit:OutboxPollingIntervalSeconds"`
- [ ] Failed consumers retry 5 times with exponential backoff before dead-lettering
- [ ] Per-consumer dead-letter exchange (e.g. `audit-log_dead_letter`, `quota-notification_dead_letter`)
- [ ] Dead-letter triggers: structured `Error` log + `Notification` DB row via `IConsumer<Fault<TEvent>>`

## Test Cases

- **TC-001**: Publish event → consumer receives it within 200ms
- **TC-002**: Simulate process restart mid-dispatch → event redelivered to consumers
- **TC-003**: Consumer throws exception → retry; after 10 retries → dead-letter entry
- **TC-004**: Two events published in same transaction → both delivered

## Implementation Tasks

- [ ] Install MassTransit packages
- [ ] Define all domain event records in `Strg.Core/Events/`
- [ ] Configure MassTransit in `Program.cs`
- [ ] Create `StrgDbContext` configuration for outbox tables
- [ ] Create placeholder consumers (STRG-062 through STRG-066 implement them)
- [ ] Write integration test for outbox reliability

## Security Review Checklist

- [ ] Event payloads do not contain file contents (only metadata: IDs, sizes)
- [ ] `TenantId` included in every event for proper isolation
- [ ] Dead-letter events accessible only to admin

## Definition of Done

- [ ] Outbox tables in migration
- [ ] Event published and consumed in integration test
- [ ] Retry logic verified
