---
id: STRG-063
title: Implement SearchIndexConsumer placeholder
milestone: v0.1
priority: medium
status: open
type: implementation
labels: [events, search, masstransit]
depends_on: [STRG-061]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-063: Implement SearchIndexConsumer placeholder

## Summary

Implement a no-op `SearchIndexConsumer` that receives `FileUploadedEvent` and `FileDeletedEvent`. In v0.1 the consumer logs the event but does nothing — full search indexing is added in v0.2 when the `ISearchProvider` plugin interface ships.

## Background

The consumer must be registered in v0.1 to ensure the message routing table is complete and MassTransit does not discard `FileUploadedEvent` / `FileDeletedEvent` messages. In v0.2 the body will be replaced with an actual search provider call.

## Technical Specification

### File: `src/Strg.Infrastructure/Consumers/SearchIndexConsumer.cs`

```csharp
public class SearchIndexConsumer :
    IConsumer<FileUploadedEvent>,
    IConsumer<FileDeletedEvent>
{
    private readonly ILogger<SearchIndexConsumer> _logger;

    public SearchIndexConsumer(ILogger<SearchIndexConsumer> logger)
    {
        _logger = logger;
    }

    public Task Consume(ConsumeContext<FileUploadedEvent> context)
    {
        _logger.LogDebug(
            "SearchIndexConsumer: file.uploaded fileId={FileId} (indexing deferred to v0.2)",
            context.Message.FileId);
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<FileDeletedEvent> context)
    {
        _logger.LogDebug(
            "SearchIndexConsumer: file.deleted fileId={FileId} (indexing deferred to v0.2)",
            context.Message.FileId);
        return Task.CompletedTask;
    }
}
```

### Future v0.2 body (for reference):

```csharp
// Will replace the log statement with:
var provider = _serviceProvider.GetRequiredService<ISearchProvider>();
await provider.IndexAsync(context.Message.FileId, context.CancellationToken);
```

## Acceptance Criteria

- [ ] Consumer registered in MassTransit and receives both event types
- [ ] No exceptions thrown — consumer is a no-op in v0.1
- [ ] Debug log line emitted for each received event
- [ ] Consumer does NOT call any storage or DB operations

## Test Cases

- **TC-001**: Publish `FileUploadedEvent` → consumer receives it, no exception thrown
- **TC-002**: Publish `FileDeletedEvent` → consumer receives it, no exception thrown
- **TC-003**: Consumer does not call `ISearchProvider` (service not registered in v0.1)

## Implementation Tasks

- [ ] Create `SearchIndexConsumer.cs` in `Strg.Infrastructure/Consumers/`
- [ ] Register consumer in MassTransit config (STRG-061 `AddConsumer<SearchIndexConsumer>()`)
- [ ] Add `TODO: v0.2` comment referencing ISearchProvider

## Testing Tasks

- [ ] Unit test verifying no exceptions on both event types
- [ ] Verify debug log message contains `fileId`

## Security Review Checklist

- [ ] Consumer does not log file paths or content metadata (only IDs)

## Code Review Checklist

- [ ] Class is `sealed`
- [ ] `TODO: v0.2` comment is present and links to the search provider issue

## Definition of Done

- [ ] Consumer registered, receives events, does not throw
