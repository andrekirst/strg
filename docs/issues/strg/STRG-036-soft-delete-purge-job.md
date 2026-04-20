---
id: STRG-036
title: Implement soft-delete purge background job with configurable retention hierarchy
milestone: v0.1
priority: medium
status: open
type: implementation
labels: [infrastructure, files, background-jobs]
depends_on: [STRG-031, STRG-004]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-036: Implement soft-delete purge background job

## Summary

Soft-deleted files are not immediately removed from storage. A background job runs periodically to permanently purge files that have exceeded their retention window. Retention is configurable at four levels with a resolution hierarchy: **Folder → Drive → User → Global default**.

## Background / Context

Soft-delete gives users a recycle-bin-style grace period before files are permanently gone. The retention period is configurable at multiple levels: the folder's setting overrides the drive's setting, which overrides the user's setting, which falls back to the global default (30 days).

## Technical Specification

### Retention resolution (most-specific wins):

```
folder.SoftDeleteRetentionDays  →  drive.SoftDeleteRetentionDays
→ user.SoftDeleteRetentionDays  →  config["Storage:SoftDeleteRetentionDays"] (default: 30)
```

### File: `src/Strg.Infrastructure/BackgroundJobs/SoftDeletePurgeJob.cs`

```csharp
public sealed class SoftDeletePurgeJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<SoftDeletePurgeJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await PurgeExpiredFilesAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task PurgeExpiredFilesAsync(CancellationToken ct)
    {
        var globalDefault = config.GetValue("Storage:SoftDeleteRetentionDays", 30);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();

        // Load deleted files with their drive + user + folder retention settings
        var candidates = await db.Files
            .IgnoreQueryFilters()  // must see soft-deleted rows
            .Where(f => f.IsDeleted)
            .Include(f => f.Drive)
            .Include(f => f.CreatedByUser)
            .ToListAsync(ct);

        foreach (var file in candidates)
        {
            var retentionDays = ResolveRetention(file, globalDefault);
            var purgeAfter = file.DeletedAt!.Value.AddDays(retentionDays);
            if (DateTimeOffset.UtcNow < purgeAfter) continue;

            // Permanent deletion: remove storage file + all versions + quota release
            await PurgeFileAsync(file, db, scope.ServiceProvider, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private static int ResolveRetention(FileItem file, int globalDefault)
    {
        // Folder → Drive → User → Global
        if (file.IsDirectory == false && file.ParentId.HasValue)
        {
            // Check parent folder retention (stored on FileItem where IsDirectory = true)
            // Implementation looks up parent folder's SoftDeleteRetentionDays
        }
        return file.Drive?.SoftDeleteRetentionDays
            ?? file.CreatedByUser?.SoftDeleteRetentionDays
            ?? globalDefault;
    }
}
```

### Retention settings on entities:

```csharp
// On Drive entity:
public int? SoftDeleteRetentionDays { get; set; }  // null = use user/global

// On User entity:
public int? SoftDeleteRetentionDays { get; set; }  // null = use global default

// On FileItem (folders only — IsDirectory = true):
public int? SoftDeleteRetentionDays { get; set; }  // null = defer to drive/user/global
```

### Global config:
```json
{
  "Storage": {
    "SoftDeleteRetentionDays": 30
  }
}
```

### Purge schedule: every hour.

## Acceptance Criteria

- [ ] Files deleted more than N days ago (per resolved retention) are permanently purged
- [ ] Retention resolution: Folder overrides Drive overrides User overrides Global
- [ ] Purge removes: DB row (FileItem), all FileVersion rows, all FileKey rows, storage files
- [ ] Quota is reduced for the user when a file is purged
- [ ] `IgnoreQueryFilters()` used to query soft-deleted rows
- [ ] Files within retention window are NOT purged
- [ ] Global default is 30 days, configurable via `"Storage:SoftDeleteRetentionDays"`
- [ ] Folder-level retention stored on `FileItem.SoftDeleteRetentionDays` where `IsDirectory = true`

## Test Cases

- **TC-001**: File deleted 31 days ago, global retention 30d → purged
- **TC-002**: File deleted 29 days ago, global retention 30d → NOT purged
- **TC-003**: File on drive with `SoftDeleteRetentionDays = 7`, deleted 8 days ago → purged (drive overrides global)
- **TC-004**: File in folder with `SoftDeleteRetentionDays = 60`, deleted 40 days ago → NOT purged (folder overrides drive)
- **TC-005**: Purged file → quota decremented for owning user

## Implementation Tasks

- [ ] Add `SoftDeleteRetentionDays` nullable column to `Drive`, `User`, and `FileItem` (folders only)
- [ ] Create `SoftDeletePurgeJob.cs` in `Strg.Infrastructure/BackgroundJobs/`
- [ ] Implement `ResolveRetention` hierarchy (folder → drive → user → global)
- [ ] Implement `PurgeFileAsync` (delete versions, file keys, storage files, DB row)
- [ ] Add quota reduction on purge via `IQuotaService`
- [ ] Register job as `IHostedService` in `Program.cs`

## Security Review Checklist

- [ ] `IgnoreQueryFilters()` only used inside the purge job — no general exposure
- [ ] Purge is irreversible — add structured log at `Warning` level before each purge
- [ ] Quota reduction is atomic (concurrent purge and upload handled correctly)

## Definition of Done

- [ ] Purge job runs and removes files past retention
- [ ] Hierarchy resolution tested for all four levels
- [ ] Quota reduction verified in integration test
