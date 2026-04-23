---
id: STRG-062
title: Implement AuditLogConsumer for domain events
milestone: v0.1
priority: high
status: done
type: implementation
labels: [events, audit, masstransit]
depends_on: [STRG-061, STRG-003, STRG-004]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-062: Implement AuditLogConsumer for domain events

## Summary

Implement the `AuditLogConsumer` that receives domain events from the MassTransit Outbox and writes append-only audit log entries to the `audit_entries` table. Each domain event type maps to a specific action string.

## Technical Specification

### File: `src/Strg.Infrastructure/Consumers/AuditLogConsumer.cs`

```csharp
public class AuditLogConsumer :
    IConsumer<FileUploadedEvent>,
    IConsumer<FileDeletedEvent>,
    IConsumer<FileMovedEvent>
{
    private readonly StrgDbContext _db;
    private readonly ILogger<AuditLogConsumer> _logger;

    public AuditLogConsumer(StrgDbContext db, ILogger<AuditLogConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<FileUploadedEvent> context)
    {
        var msg = context.Message;
        _db.AuditEntries.Add(new AuditEntry
        {
            TenantId = msg.TenantId,
            UserId = msg.UserId,
            Action = "file.uploaded",
            ResourceId = msg.FileId,
            ResourceType = "FileItem",
            MetadataJson = JsonSerializer.Serialize(new { msg.DriveId, msg.Size, msg.MimeType })
        });
        await _db.SaveChangesAsync(context.CancellationToken);
    }

    public async Task Consume(ConsumeContext<FileDeletedEvent> context)
    {
        var msg = context.Message;
        _db.AuditEntries.Add(new AuditEntry
        {
            TenantId = msg.TenantId,
            UserId = msg.UserId,
            Action = "file.deleted",
            ResourceId = msg.FileId,
            ResourceType = "FileItem",
            MetadataJson = JsonSerializer.Serialize(new { msg.DriveId })
        });
        await _db.SaveChangesAsync(context.CancellationToken);
    }

    public async Task Consume(ConsumeContext<FileMovedEvent> context)
    {
        var msg = context.Message;
        _db.AuditEntries.Add(new AuditEntry
        {
            TenantId = msg.TenantId,
            UserId = msg.UserId,
            Action = "file.moved",
            ResourceId = msg.FileId,
            ResourceType = "FileItem",
            MetadataJson = JsonSerializer.Serialize(new { msg.OldPath, msg.NewPath, msg.DriveId })
        });
        await _db.SaveChangesAsync(context.CancellationToken);
    }
}
```

### `AuditEntry` entity (in `Strg.Core/Domain/AuditEntry.cs`):

```csharp
public class AuditEntry : Entity
{
    public Guid TenantId { get; init; }
    public Guid UserId { get; init; }
    public string Action { get; init; } = string.Empty;
    public Guid ResourceId { get; init; }
    public string ResourceType { get; init; } = string.Empty;
    public string? IpAddress { get; init; }
    public string? MetadataJson { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public Guid EventId { get; init; }  // unique constraint — idempotency key for at-least-once delivery
}
```

### Action strings convention:

| Event | Action |
|-------|--------|
| FileUploadedEvent | `file.uploaded` |
| FileDeletedEvent | `file.deleted` |
| FileMovedEvent | `file.moved` |
| BackupCompletedEvent | `backup.completed` |
| QuotaWarningEvent | `quota.warning` |

### EF Core configuration:

```csharp
// In StrgDbContext.OnModelCreating:
modelBuilder.Entity<AuditEntry>(e =>
{
    e.HasKey(x => x.Id);
    e.HasIndex(x => new { x.TenantId, x.Timestamp });
    e.HasIndex(x => new { x.UserId, x.Timestamp });
    e.HasIndex(x => new { x.ResourceId, x.ResourceType });
    // No soft-delete filter: audit entries are never deleted
    // No tenant filter: admin queries span all entries
});
```

## Acceptance Criteria

- [ ] `FileUploadedEvent` consumed → `AuditEntry` with action `file.uploaded` in DB
- [ ] `FileDeletedEvent` consumed → `AuditEntry` with action `file.deleted` in DB
- [ ] `FileMovedEvent` consumed → `AuditEntry` with action `file.moved` in DB
- [ ] `MetadataJson` contains relevant context (driveId, paths, sizes)
- [ ] `AuditEntry.EventId` has a unique DB constraint — duplicate message delivery hits the constraint and is silently swallowed (idempotency via `ON CONFLICT DO NOTHING`)
- [ ] Consumer handles `DbUpdateException` → logs and rethrows (MassTransit retries)
- [ ] Audit entries are NOT soft-deletable (no `DeletedAt` column)
- [ ] `Timestamp` is always UTC

## Test Cases

- **TC-001**: Publish `FileUploadedEvent` → audit entry with action `file.uploaded` exists
- **TC-002**: Publish `FileDeletedEvent` → audit entry with action `file.deleted` exists
- **TC-003**: Consumer `Consume` throws `DbUpdateException` → MassTransit retries
- **TC-004**: `MetadataJson` for `FileMovedEvent` contains `oldPath` and `newPath`
- **TC-005**: Consumer receives two events in sequence → two audit entries created

## Implementation Tasks

- [ ] Create `AuditEntry` entity in `Strg.Core/Domain/`
- [ ] Add `DbSet<AuditEntry>` to `StrgDbContext`
- [ ] Configure EF Core indexes (no soft-delete filter)
- [ ] Create `AuditLogConsumer.cs` in `Strg.Infrastructure/Consumers/`
- [ ] Register consumer in MassTransit config (STRG-061)
- [ ] Create migration for `audit_entries` table

## Testing Tasks

- [ ] Integration test: publish `FileUploadedEvent` → verify audit entry
- [ ] Unit test: consumer error → exception rethrown
- [ ] Verify `Timestamp` stored as UTC in both SQLite and PostgreSQL

## Security Review Checklist

- [ ] `IpAddress` comes from request context, not from event payload (prevents spoofing)
- [ ] `MetadataJson` does NOT contain file contents or credentials
- [ ] Audit entries have no DELETE endpoint
- [ ] Admin-only read access to audit log (verified in API layer, not here)

## Code Review Checklist

- [ ] No `SaveChangesAsync` inside loops
- [ ] `AuditEntry` all properties are `init` (immutable once created)
- [ ] `JsonSerializer.Serialize` uses camelCase naming policy

## Definition of Done

- [ ] All three event types produce audit entries
- [ ] Integration test passes with SQLite in-process transport
