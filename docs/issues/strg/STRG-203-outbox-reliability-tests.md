---
id: STRG-203
title: MassTransit Outbox reliability integration tests
milestone: v0.1
priority: high
status: open
type: testing
labels: [testing, events, masstransit]
depends_on: [STRG-200, STRG-061, STRG-062, STRG-065]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-203: MassTransit Outbox reliability integration tests

## Summary

Write integration tests verifying the outbox pattern's reliability guarantees: atomicity (event and DB write in same transaction), delivery after simulated failures, exactly-once processing, and retry behavior for failing consumers.

## Technical Specification

### Test class: `tests/Strg.Integration.Tests/Events/OutboxReliabilityTests.cs`

Tests use `IOutboxFlusher.FlushAsync()` to trigger immediate outbox dispatch — no `Task.Delay` or polling. `IOutboxFlusher` is registered as a test-only singleton in `StrgWebApplicationFactory` that calls the MassTransit outbox delivery service directly.

```csharp
public class OutboxReliabilityTests : IClassFixture<DatabaseFixture>
{
    private readonly DatabaseFixture _fixture;
    private readonly IOutboxFlusher _outboxFlusher;

    public OutboxReliabilityTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        _outboxFlusher = fixture.Factory.Services.GetRequiredService<IOutboxFlusher>();
    }

    [Fact]
    public async Task FileUpload_EventAndFileItemCommittedAtomically()
    {
        // Arrange: upload a file in a transaction
        // Act: verify FileItem and outbox message both exist in DB before dispatch
        // Assert: both committed atomically — do NOT call FlushAsync yet
    }

    [Fact]
    public async Task Event_DeliveredToConsumer_AfterFlush()
    {
        // Publish event via the API (which writes to outbox, not direct bus)
        var response = await _fixture.Factory.CreateClient()
            .WithAuth(token)
            .PostAsync("/api/v1/files/upload-complete", ...);

        // Flush outbox synchronously — no Task.Delay needed
        await _outboxFlusher.FlushAsync();

        // Assert consumed
        var auditEntry = await db.AuditLog.SingleOrDefaultAsync(e => e.FileId == fileId);
        Assert.NotNull(auditEntry);
    }

    [Fact]
    public async Task FailingConsumer_RetriesAndDeadLetters()
    {
        // Register a consumer that throws on first N attempts
        // Flush — each flush triggers one delivery attempt
        for (int i = 0; i < 5; i++) // 5 retries confirmed
            await _outboxFlusher.FlushAsync();
        // Verify: after 5 retries, message in dead letter (Fault<T> consumed)
    }

    [Fact]
    public async Task TwoEventsInSameTransaction_BothDelivered()
    {
        // Publish 2 events in same scope
        await _outboxFlusher.FlushAsync();
        // Assert both consumed
    }

    [Fact]
    public async Task AuditLogConsumer_CreatesEntryForEachEvent()
    {
        // Two operations that each emit an event
        await _outboxFlusher.FlushAsync();

        // Assert 2 audit entries in DB
        var count = await db.AuditLog.CountAsync();
        Assert.Equal(2, count);
    }
}
```

### Testing retry behavior:

```csharp
public class FailingConsumer : IConsumer<FileUploadedEvent>
{
    private static int _callCount;

    public Task Consume(ConsumeContext<FileUploadedEvent> ctx)
    {
        if (Interlocked.Increment(ref _callCount) <= 3)
            throw new InvalidOperationException("simulated failure");
        return Task.CompletedTask;
    }
}
```

## Acceptance Criteria

- [ ] `IOutboxFlusher.FlushAsync()` used for all outbox delivery assertions — no `Task.Delay` or polling
- [ ] Failing consumer retries 5 times (configured in STRG-061), then dead-letters
- [ ] Two events in same transaction both delivered after single flush
- [ ] `AuditLogConsumer` creates DB entries for each received event
- [ ] Dead-letter behavior verified after 5 retries (not 10)

## Test Cases

- **TC-001**: Publish event via API → `FlushAsync()` → consumer receives, DB entry created
- **TC-002**: Consumer throws → retry; after 5 → dead-letter (`IConsumer<Fault<T>>` triggered)
- **TC-003**: Two events same TX → `FlushAsync()` → both consumed
- **TC-004**: `AuditLogConsumer` → 2 audit entries for 2 events

## Implementation Tasks

- [ ] Create `OutboxReliabilityTests.cs`
- [ ] Create `FailingConsumer` test double
- [ ] Inject `IOutboxFlusher` from `StrgWebApplicationFactory` services
- [ ] Configure MassTransit test harness with retry policy (5 retries)
- [ ] Verify dead-letter via `IConsumer<Fault<T>>` registration in test

## Definition of Done

- [ ] Atomicity, delivery speed, retry, and dead-letter all tested
