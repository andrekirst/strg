---
id: STRG-066
title: Implement FileSubscriptions GraphQL type
milestone: v0.1
priority: high
status: done
type: implementation
labels: [graphql, subscriptions, real-time]
depends_on: [STRG-049, STRG-065, STRG-050]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-066: Implement FileSubscriptions GraphQL type

## Summary

Implement the Hot Chocolate `FileSubscriptions` type that exposes a `fileEvents(driveId: ID!)` subscription field. The payload includes the full `FileItem` object (not just the ID), enabling clients to update their UI without a follow-up query. `TenantId` is never exposed.

## Technical Specification

### Schema:

```graphql
type Subscription {
  fileEvents(driveId: ID!): FileEvent!
}

type FileEvent {
  eventType: FileEventType!
  file: FileItem!       # full FileItem resolved via DataLoader
  driveId: ID!
  occurredAt: DateTime!
  # TenantId — NEVER exposed
}

enum FileEventType {
  UPLOADED
  DELETED
  MOVED
  COPIED
  RENAMED
}
```

### File: `src/Strg.GraphQL/Subscriptions/FileSubscriptions.cs`

```csharp
[ExtendObjectType("Subscription")]
public sealed class FileSubscriptions
{
    [Subscribe]
    [Topic($"{{{nameof(driveId)}}}")]  // resolves to "file-events:{driveId}" via Topics helper
    [Authorize(Policy = "FilesRead")]
    public async Task<FileEventPayload> OnFileEvent(
        ID driveId,
        [EventMessage] FileEvent fileEvent,
        [GlobalState("tenantId")] Guid tenantId,
        [Service] FileItemByIdDataLoader fileLoader,
        CancellationToken ct)
    {
        // Tenant isolation guard — discard events that don't belong to this tenant
        if (fileEvent.TenantId != tenantId)
            throw new UnauthorizedAccessException("Subscription event tenant mismatch.");

        var file = await fileLoader.LoadAsync(fileEvent.FileId, ct);
        return new FileEventPayload(fileEvent.EventType, file!, fileEvent.DriveId, fileEvent.OccurredAt);
    }

    [SubscribeResolver]
    public ValueTask<ISourceStream<FileEvent>> SubscribeToFileEventsAsync(
        ID driveId,
        [Service] ITopicEventReceiver receiver,
        CancellationToken ct)
        => receiver.SubscribeAsync<FileEvent>(Topics.FileEvents((Guid)driveId), ct);
}
```

### File: `src/Strg.GraphQL/Types/FileEventType.cs`

```csharp
public sealed class FileEventOutputType : ObjectType<FileEventPayload>
{
    protected override void Configure(IObjectTypeDescriptor<FileEventPayload> descriptor)
    {
        descriptor.Field(e => e.EventType);
        descriptor.Field(e => e.File);        // full FileItem object
        descriptor.Field(e => e.DriveId);
        descriptor.Field(e => e.OccurredAt);
        // TenantId is on the internal FileEvent DTO, not on FileEventPayload — never exposed
    }
}

// Payload record (no TenantId)
public sealed record FileEventPayload(
    FileEventType EventType,
    FileItem File,
    Guid DriveId,
    DateTimeOffset OccurredAt
);
```

### DataLoader usage

The subscription resolver loads the full `FileItem` via `FileItemByIdDataLoader`. This ensures:
1. The full file object is available in the subscription payload (no follow-up query needed by client)
2. Batch loading if multiple subscribers receive events for the same files simultaneously

### WebSocket subscription example (graphql-ws protocol):

```json
{
  "type": "subscribe",
  "id": "1",
  "payload": {
    "query": "subscription { fileEvents(driveId: \"abc\") { eventType driveId occurredAt file { id name size mimeType isFolder } } }"
  }
}
```

### Topic resolution:

`Topics.FileEvents(driveId)` in `SubscribeToFileEventsAsync` must match the topic used in `GraphQLSubscriptionPublisher` (STRG-065).

## Acceptance Criteria

- [ ] WebSocket subscription to `fileEvents(driveId: "...")` receives events within 200ms
- [ ] Payload includes full `FileItem` object (not just `fileId`)
- [ ] `FileEvent.TenantId` NOT exposed in schema (absent from `FileEventPayload`)
- [ ] Subscription requires `files.read` authorization
- [ ] Event for drive in different tenant → connection receives no events (tenant mismatch guard)
- [ ] `FileEventType.UPLOADED`, `DELETED`, `MOVED`, `COPIED`, `RENAMED` correctly serialized
- [ ] `FileEventOutputType` implements no `Node` interface (it's a payload, not an entity)

## Test Cases

- **TC-001**: Client subscribes → `FileUploadedEvent` published → subscription message received with full `file` object
- **TC-002**: Client subscribes to drive A → event for drive B → no message received
- **TC-003**: Unauthenticated WebSocket → subscription rejected with `UNAUTHENTICATED`
- **TC-004**: Two subscribers to same drive → both receive the event
- **TC-005**: `FileEvent.tenantId` not present in subscription payload (schema introspection)
- **TC-006**: `file.isFolder` correct in subscription payload

## Implementation Tasks

- [ ] Create `FileSubscriptions.cs` in `src/Strg.GraphQL/Subscriptions/`
- [ ] Create `FileEventOutputType.cs` in `src/Strg.GraphQL/Types/` (ignores TenantId)
- [ ] Create `FileEventPayload.cs` record in `src/Strg.GraphQL/Payloads/`
- [ ] `FileItemByIdDataLoader` used in subscription resolver
- [ ] Types auto-discovered by `AddTypes()` — no manual registration
- [ ] Verify `Topics.FileEvents()` used consistently in publisher and subscriber

## Security Review Checklist

- [ ] `FileEventPayload` has no `TenantId` field
- [ ] `[Authorize]` on subscription field (auth checked on subscribe)
- [ ] Tenant mismatch guard in resolver (belt-and-suspenders)
- [ ] `UnauthorizedAccessException` in resolver closes subscription cleanly

## Code Review Checklist

- [ ] `Topics.FileEvents()` used in both `SubscribeToFileEventsAsync` and publisher (STRG-065) — no duplicate strings
- [ ] `[EventMessage]` attribute on the `FileEvent` parameter
- [ ] `FileEventPayload` is a `sealed record` (immutable)

## Definition of Done

- [ ] Subscription receives full `FileItem` in integration test
- [ ] Tenant isolation verified
- [ ] `TenantId` absent from schema
