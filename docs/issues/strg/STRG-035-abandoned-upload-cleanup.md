---
id: STRG-035
title: Implement abandoned TUS upload cleanup background job
milestone: v0.1
priority: medium
status: open
type: implementation
labels: [infrastructure, upload, background-jobs]
depends_on: [STRG-034, STRG-032]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-035: Implement abandoned TUS upload cleanup background job

## Summary

When a TUS upload is initiated (POST), quota is reserved atomically. If the upload is never completed (browser closed, network dropped, client crashed), the reserved quota is permanently stuck. This background job detects expired TUS uploads and releases their quota reservations.

## Background / Context

tusdotnet marks uploads as expired via the `Upload-Expires` header. The TUS spec defines upload expiration — if a client doesn't complete the upload before the expiry time, the server may clean it up. strg reserves quota at upload start (POST), so orphaned uploads hold quota that should be freed.

## Technical Specification

### File: `src/Strg.Infrastructure/BackgroundJobs/AbandonedUploadCleanupJob.cs`

```csharp
public sealed class AbandonedUploadCleanupJob(
    IServiceScopeFactory scopeFactory,
    ILogger<AbandonedUploadCleanupJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await CleanupExpiredUploadsAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task CleanupExpiredUploadsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var quotaService = scope.ServiceProvider.GetRequiredService<IQuotaService>();
        var storageRegistry = scope.ServiceProvider.GetRequiredService<IStorageProviderRegistry>();

        var expired = await db.PendingUploads
            .Where(u => u.ExpiresAt < DateTimeOffset.UtcNow && !u.IsCompleted)
            .ToListAsync(ct);

        foreach (var upload in expired)
        {
            await quotaService.ReleaseReservationAsync(upload.UserId, upload.ReservedBytes, ct);
            // Delete temp file from storage
            var provider = storageRegistry.Resolve(upload.ProviderType, upload.ProviderConfig);
            await provider.DeleteAsync(upload.TempPath, ct);
            db.PendingUploads.Remove(upload);
            logger.LogInformation("Cleaned up expired upload {UploadId} for user {UserId}", upload.Id, upload.UserId);
        }

        await db.SaveChangesAsync(ct);
    }
}
```

### `PendingUpload` entity:

```csharp
public sealed class PendingUpload : TenantedEntity
{
    public Guid UserId { get; init; }
    public Guid DriveId { get; init; }
    public long ReservedBytes { get; init; }
    public required string TempPath { get; init; }
    public required string ProviderType { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public bool IsCompleted { get; set; }
}
```

### Cleanup schedule: every 5 minutes (not configurable in v0.1).

### Registration:
```csharp
builder.Services.AddHostedService<AbandonedUploadCleanupJob>();
```

## Acceptance Criteria

- [ ] Background job runs every 5 minutes
- [ ] Expired `PendingUpload` rows are detected (where `ExpiresAt < now AND IsCompleted = false`)
- [ ] Quota reservation released for each expired upload via `IQuotaService.ReleaseReservationAsync()`
- [ ] Temp file deleted from storage provider
- [ ] `PendingUpload` row removed from DB
- [ ] Completed uploads (`IsCompleted = true`) are NOT cleaned up
- [ ] Job handles storage provider errors gracefully (logs and continues)

## Test Cases

- **TC-001**: Create pending upload with `ExpiresAt = now - 1min` → cleanup job removes it and releases quota
- **TC-002**: Completed upload not cleaned up
- **TC-003**: Multiple expired uploads → all cleaned up in single job run
- **TC-004**: Storage provider throws on delete → job continues with next upload, logs error

## Implementation Tasks

- [ ] Create `PendingUpload.cs` entity in `Strg.Core/Domain/`
- [ ] Create `AbandonedUploadCleanupJob.cs` in `Strg.Infrastructure/BackgroundJobs/`
- [ ] Add `IQuotaService.ReleaseReservationAsync()` method
- [ ] Register job as `IHostedService` in `Program.cs`
- [ ] Add `PendingUploads` to `StrgDbContext`

## Security Review Checklist

- [ ] Job uses scoped `IServiceScopeFactory` (not singleton DbContext)
- [ ] Quota release is atomic (SQL UPDATE with WHERE clause)
- [ ] Job does not expose PendingUpload data to external callers

## Definition of Done

- [ ] Job runs and cleans expired uploads
- [ ] Quota correctly released in integration test
