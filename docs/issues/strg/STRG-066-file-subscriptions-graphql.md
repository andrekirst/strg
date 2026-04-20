---
id: STRG-066
title: Implement FileSubscriptions GraphQL type
milestone: v0.1
priority: high
status: open
type: implementation
labels: [graphql, subscriptions, real-time]
depends_on: [STRG-049, STRG-065, STRG-050]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-066: Implement FileSubscriptions GraphQL type

## Summary

Implement the Hot Chocolate `FileSubscriptions` type that exposes a `fileEvents(driveId: UUID!)` subscription field. Clients subscribe over WebSocket and receive `FileEvent` notifications whenever a file in the specified drive is uploaded, deleted, or moved.

## Technical Specification

### File: `src/Strg.GraphQL/Subscriptions/FileSubscriptions.cs`

```csharp
[ExtendObjectType("Subscription")]
public class FileSubscriptions
{
    [Subscribe]
    [Topic("file-events:{driveId}")]
    [Authorize(Policy = "FilesRead")]
    public FileEvent OnFileEvent(
        Guid driveId,
        [EventMessage] FileEvent fileEvent,
        [GlobalState("userId")] Guid userId,
        [GlobalState("tenantId")] Guid tenantId)
    {
        // Tenant isolation guard — discard events from other tenants
        if (fileEvent.TenantId != tenantId)
        {
            throw new UnauthorizedAccessException(
                "Subscription event tenant mismatch.");
        }

        return fileEvent;
    }
}
```

### File: `src/Strg.GraphQL/Types/FileEventType.cs`

```csharp
public class FileEventType : ObjectType<FileEvent>
{
    protected override void Configure(IObjectTypeDescriptor<FileEvent> descriptor)
    {
        descriptor.Field(e => e.TenantId).Ignore(); // never expose to clients
    }
}
```

### GraphQL schema exposed:

```graphql
type Subscription {
  fileEvents(driveId: UUID!): FileEventPayload!
}

type FileEventPayload {
  type: FileEventType!
  fileId: UUID!
  driveId: UUID!
  userId: UUID!
  oldPath: String
  newPath: String
  occurredAt: DateTime!
}

enum FileEventType {
  UPLOADED
  DELETED
  MOVED
}
```

### WebSocket subscription example (graphql-ws protocol):

```json
{
  "type": "subscribe",
  "id": "1",
  "payload": {
    "query": "subscription { fileEvents(driveId: \"...\") { type fileId occurredAt } }"
  }
}
```

### Topic resolution:

Hot Chocolate resolves `[Topic("file-events:{driveId}")]` using the `driveId` argument. The publisher (STRG-065) must use the exact same topic format string.

## Acceptance Criteria

- [ ] WebSocket subscription to `fileEvents(driveId: "...")` receives events within 200ms of domain event publish
- [ ] `FileEvent.TenantId` NOT exposed in schema (ignored in `FileEventType`)
- [ ] Subscription requires `files.read` authorization
- [ ] Subscription for drive in different tenant → connection receives no events (tenant mismatch guard)
- [ ] `FileEventType.UPLOADED`, `DELETED`, `MOVED` correctly serialized
- [ ] Subscription over plain WebSocket (`ws://`) for dev; `wss://` in production

## Test Cases

- **TC-001**: Client subscribes → `FileUploadedEvent` published → subscription message received
- **TC-002**: Client subscribes to drive A → event for drive B → no message received by client
- **TC-003**: Unauthenticated WebSocket → subscription rejected with `UNAUTHENTICATED`
- **TC-004**: Two subscribers to same drive → both receive the event
- **TC-005**: `FileEvent.tenantId` not present in subscription payload

## Implementation Tasks

- [ ] Create `FileSubscriptions.cs` in `Strg.GraphQL/Subscriptions/`
- [ ] Create `FileEventType.cs` in `Strg.GraphQL/Types/` (ignores `TenantId`)
- [ ] Register `FileSubscriptions` type in Hot Chocolate setup (STRG-049)
- [ ] Register `FileEventType` in Hot Chocolate setup
- [ ] Ensure topic string matches `GraphQLSubscriptionPublisher` (STRG-065)

## Testing Tasks

- [ ] Integration test using Hot Chocolate's WebSocket test client
- [ ] Verify `tenantId` field absent from schema introspection
- [ ] Verify event received within 200ms (timing test with `Task.Delay` and `Timeout`)

## Security Review Checklist

- [ ] `TenantId` field ignored in schema (never exposed to client)
- [ ] `[Authorize]` attribute on subscription field (auth check on subscribe)
- [ ] Tenant mismatch guard in resolver (belt-and-suspenders over topic naming)
- [ ] Rate limit WebSocket connection requests

## Code Review Checklist

- [ ] Topic format string matches publisher exactly (consider extracting to shared constant)
- [ ] `UnauthorizedAccessException` in resolver closes the subscription cleanly
- [ ] `[EventMessage]` attribute on the `FileEvent` parameter

## Definition of Done

- [ ] Subscription receives events in integration test
- [ ] Tenant isolation verified
- [ ] `tenantId` absent from schema
