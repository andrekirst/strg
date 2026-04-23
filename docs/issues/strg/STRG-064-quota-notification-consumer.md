---
id: STRG-064
title: Implement QuotaNotificationConsumer
milestone: v0.1
priority: medium
status: done
type: implementation
labels: [events, quota, masstransit]
depends_on: [STRG-061, STRG-032]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-064: Implement QuotaNotificationConsumer

## Summary

Implement the `QuotaNotificationConsumer` that receives `QuotaWarningEvent` (fired when a user's storage usage crosses 80% or 95% of their quota) and delivers the notification via two channels: a GraphQL subscription event (for live clients) AND a `Notification` DB row (for clients that reconnect later).

## Technical Specification

### File: `src/Strg.Infrastructure/Consumers/QuotaNotificationConsumer.cs`

```csharp
public sealed class QuotaNotificationConsumer : IConsumer<QuotaWarningEvent>
{
    private readonly ILogger<QuotaNotificationConsumer> _logger;

    public QuotaNotificationConsumer(ILogger<QuotaNotificationConsumer> logger)
    {
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<QuotaWarningEvent> context)
    {
        var msg = context.Message;
        double usagePercent = msg.QuotaBytes > 0
            ? (double)msg.UsedBytes / msg.QuotaBytes * 100
            : 100;

        _logger.LogWarning(
            "Quota warning: tenantId={TenantId} userId={UserId} used={UsedBytes} quota={QuotaBytes} ({UsagePercent:F1}%)",
            msg.TenantId, msg.UserId, msg.UsedBytes, msg.QuotaBytes, usagePercent);

        // 1. Write Notification DB row (for clients that reconnect later)
        _db.Notifications.Add(new Notification
        {
            UserId = msg.UserId,
            TenantId = msg.TenantId,
            Type = "quota.warning",
            PayloadJson = JsonSerializer.Serialize(new { msg.UsedBytes, msg.QuotaBytes, usagePercent })
        });
        await _db.SaveChangesAsync(context.CancellationToken);

        // 2. Push to GraphQL subscription (for live clients)
        await _topicEventSender.SendAsync("QuotaWarning", msg, context.CancellationToken);
    }
}
```

### Where `QuotaWarningEvent` is fired (in `QuotaService.CommitAsync`):

```csharp
// After committing quota usage:
double pct = (double)newUsed / quotaBytes;
if (pct >= 0.80)
{
    await _publishEndpoint.Publish(new QuotaWarningEvent(
        TenantId: tenantId,
        UserId: userId,
        UsedBytes: newUsed,
        QuotaBytes: quotaBytes),
        ct);
}
```

### Threshold constants (in `Strg.Core/Domain/QuotaThresholds.cs`):

```csharp
public static class QuotaThresholds
{
    public const double Warning = 0.80;
    public const double Critical = 0.95;
}
```

## Acceptance Criteria

- [ ] Consumer receives `QuotaWarningEvent` and logs structured warning
- [ ] Consumer writes a `Notification` row to DB (type: `quota.warning`, includes percentage in payload)
- [ ] Consumer pushes to GraphQL subscription topic `QuotaWarning` via `ITopicEventSender`
- [ ] `QuotaWarningEvent` published when usage crosses 80% in `QuotaService`
- [ ] No exception thrown when quota bytes is 0 (edge case — infinite quota)

## Test Cases

- **TC-001**: Publish `QuotaWarningEvent` at 85% → warning log line emitted
- **TC-002**: `QuotaBytes = 0` (infinite) → no divide-by-zero exception
- **TC-003**: `QuotaService.CommitAsync` where newUsed/quota >= 0.80 → event published
- **TC-004**: `QuotaService.CommitAsync` where newUsed/quota < 0.80 → no event published

## Implementation Tasks

- [ ] Create `QuotaNotificationConsumer.cs` in `Strg.Infrastructure/Consumers/`
- [ ] Create `QuotaThresholds` constants class in `Strg.Core/Domain/`
- [ ] Add 80% threshold check in `QuotaService.CommitAsync`
- [ ] Register consumer in MassTransit config (STRG-061)

## Testing Tasks

- [ ] Unit test: percentage calculation (integer overflow safety: use `(double)usedBytes`)
- [ ] Integration test: upload file pushing user to 81% → consumer logs warning
- [ ] Unit test: `QuotaBytes = 0` → no exception

## Security Review Checklist

- [ ] Log line contains only IDs and bytes — no file paths, names, or content
- [ ] Consumer does not query DB (pull-only from event payload)

## Code Review Checklist

- [ ] Cast to `double` before division (avoids integer division)
- [ ] Consumer is `sealed`
- [ ] `TODO: v0.2` comment for email/push notification

## Definition of Done

- [ ] Consumer receives event and logs warning
- [ ] Threshold check in QuotaService fires event correctly
