---
id: STRG-032
title: Implement QuotaService — check, reserve, and commit storage quota
milestone: v0.1
priority: critical
status: open
type: implementation
labels: [core, business-logic, quota]
depends_on: [STRG-011, STRG-031]
blocks: [STRG-034]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-032: Implement QuotaService — check, reserve, and commit storage quota

## Summary

Implement `IQuotaService` which checks user quota before accepting uploads, atomically commits used bytes when uploads complete, and provides quota reporting. Must handle concurrent uploads correctly.

## Technical Specification

### File: `src/Strg.Core/Services/IQuotaService.cs`

```csharp
public interface IQuotaService
{
    Task<QuotaCheckResult> CheckAsync(Guid userId, long requiredBytes, CancellationToken ct = default);
    Task CommitAsync(Guid userId, long bytesAdded, CancellationToken ct = default);
    Task ReleaseAsync(Guid userId, long bytesReleased, CancellationToken ct = default);
    Task<QuotaInfo> GetInfoAsync(Guid userId, CancellationToken ct = default);
}

public record QuotaCheckResult(bool IsAllowed, long Available, long Quota, long Used);
public record QuotaInfo(long QuotaBytes, long UsedBytes, long FreeBytes, double UsagePercent);
```

### Concurrent upload safety:

Use `UPDATE users SET used_bytes = used_bytes + @delta WHERE id = @id AND used_bytes + @delta <= quota_bytes RETURNING used_bytes`

This is an atomic optimistic update. If it returns 0 rows, quota was exceeded.

## Acceptance Criteria

- [ ] `CheckAsync` returns `IsAllowed = false` when `usedBytes + requiredBytes > quotaBytes`
- [ ] `CommitAsync` uses an atomic SQL UPDATE (not read-then-write)
- [ ] Two concurrent uploads don't exceed quota when combined (concurrent safety)
- [ ] `ReleaseAsync` decrements `UsedBytes` when a failed upload is aborted
- [ ] `GetInfoAsync` returns accurate real-time usage
- [ ] Quota check performed on each TUS chunk (not just final completion)
- [ ] `QuotaInfo.UsagePercent` is between 0 and 100 inclusive

## Test Cases

- **TC-001**: User with 100MB quota, 90MB used → upload of 5MB → `IsAllowed = true`
- **TC-002**: User with 100MB quota, 90MB used → upload of 15MB → `IsAllowed = false`
- **TC-003**: Two concurrent 55MB uploads for a user with 100MB quota → only one succeeds
- **TC-004**: `CommitAsync` → `UsedBytes` incremented atomically
- **TC-005**: `ReleaseAsync` after abort → `UsedBytes` decremented

## Implementation Tasks

- [ ] Create `IQuotaService.cs` in `Strg.Core.Services`
- [ ] Implement `QuotaService.cs` in `Strg.Infrastructure`
- [ ] Use atomic SQL UPDATE for `CommitAsync`
- [ ] Write unit tests with mocked repository
- [ ] Write concurrency test with two parallel uploads

## Security Review Checklist

- [ ] Quota enforcement is server-side only (client-reported size not trusted for enforcement)
- [ ] Quota update is atomic (prevents race conditions via SQL update)
- [ ] `ReleaseAsync` cannot release more bytes than were reserved (underflow protection)

## Definition of Done

- [ ] Concurrency test passes
- [ ] Integrated with TUS upload handler (STRG-034)
