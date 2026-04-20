---
id: STRG-307
title: Inbox folder auto-creation service
milestone: v0.1
priority: high
status: open
type: implementation
labels: [inbox, storage, domain]
depends_on: [STRG-300, STRG-031, STRG-024, STRG-305]
blocks: [STRG-308]
assigned_agent_type: feature-dev
estimated_complexity: small
---

# STRG-307: Inbox folder auto-creation service

## Summary

Implement `IInboxFolderService`, which ensures the `/inbox` folder exists on the user's default drive. It is called once per user — either during registration or on first upload. The operation is idempotent: if the folder already exists, it returns it without error. A special `InboxFolderNotFoundException` is thrown when the user has no default drive configured.

## Technical Specification

### Interface (`src/Strg.Core/Inbox/IInboxFolderService.cs`)

```csharp
public interface IInboxFolderService
{
    /// <summary>
    /// Returns the /inbox directory FileItem on the user's default drive,
    /// creating it if it does not exist.
    /// </summary>
    /// <exception cref="NoDefaultDriveException">
    /// Thrown when the user has no drive with IsDefault = true.
    /// </exception>
    Task<FileItem> EnsureInboxFolderAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Returns the inbox folder if it exists, or null.</summary>
    Task<FileItem?> GetInboxFolderAsync(Guid userId, CancellationToken ct = default);
}
```

### Exception (`src/Strg.Core/Exceptions/NoDefaultDriveException.cs`)

```csharp
public sealed class NoDefaultDriveException(Guid userId)
    : Exception($"User '{userId}' has no default drive configured.");
```

### Implementation (`src/Strg.Infrastructure/Inbox/InboxFolderService.cs`)

```csharp
public sealed class InboxFolderService(StrgDbContext db) : IInboxFolderService
{
    private const string InboxPath = "/inbox";

    public async Task<FileItem> EnsureInboxFolderAsync(Guid userId, CancellationToken ct = default)
    {
        var defaultDrive = await db.Drives
            .FirstOrDefaultAsync(d => d.IsDefault && !d.IsDeleted, ct)
            ?? throw new NoDefaultDriveException(userId);

        var inbox = await GetInboxFolderCoreAsync(defaultDrive.Id, ct);
        if (inbox != null)
            return inbox;

        inbox = new FileItem
        {
            DriveId = defaultDrive.Id,
            TenantId = defaultDrive.TenantId,
            ParentId = null,
            Name = "inbox",
            Path = InboxPath,
            IsDirectory = true,
            IsInInbox = false,   // the inbox folder itself is not "in inbox"
            Size = 0,
            CreatedBy = userId,
            MimeType = "application/x-directory"
        };

        db.Files.Add(inbox);
        await db.SaveChangesAsync(ct);
        return inbox;
    }

    public async Task<FileItem?> GetInboxFolderAsync(Guid userId, CancellationToken ct = default)
    {
        var defaultDrive = await db.Drives
            .FirstOrDefaultAsync(d => d.IsDefault && !d.IsDeleted, ct);
        if (defaultDrive == null) return null;
        return await GetInboxFolderCoreAsync(defaultDrive.Id, ct);
    }

    private Task<FileItem?> GetInboxFolderCoreAsync(Guid driveId, CancellationToken ct) =>
        db.Files.FirstOrDefaultAsync(
            f => f.DriveId == driveId && f.Path == InboxPath && f.IsDirectory && !f.IsDeleted,
            ct);
}
```

### Registration of inbox on upload (hook point)

In the TUS upload completion handler or REST file upload endpoint, call `EnsureInboxFolderAsync` before accepting the first upload for a user:

```csharp
// In upload handler
await _inboxFolderService.EnsureInboxFolderAsync(userId, ct);
```

Also: if the file being uploaded has path `/inbox/...`, set `IsInInbox = true` and `InboxStatus = Pending` and `InboxEnteredAt = now` on the `FileItem`.

### DI registration

```csharp
services.AddScoped<IInboxFolderService, InboxFolderService>();
```

## Acceptance Criteria

- [ ] `IInboxFolderService` with `EnsureInboxFolderAsync` and `GetInboxFolderAsync` exists in `Strg.Core`
- [ ] `NoDefaultDriveException` exists in `Strg.Core/Exceptions/`
- [ ] `InboxFolderService` implementation in `Strg.Infrastructure`
- [ ] `EnsureInboxFolderAsync` is idempotent — calling twice does not create duplicate folders
- [ ] `/inbox` folder is a `FileItem` with `IsDirectory = true` and `Path = "/inbox"`
- [ ] `NoDefaultDriveException` thrown when user has no drive with `IsDefault = true`
- [ ] Registered as scoped in DI

## Test Cases

- TC-001: User with no drives → `EnsureInboxFolderAsync` throws `NoDefaultDriveException`
- TC-002: User with one default drive → `/inbox` folder is created on first call
- TC-003: `EnsureInboxFolderAsync` called twice → returns same `FileItem`, no duplicate rows
- TC-004: `GetInboxFolderAsync` returns `null` when no default drive
- TC-005: Inbox folder is `IsDirectory = true` and `IsInInbox = false`
- TC-006: Tenant isolation — `EnsureInboxFolderAsync` for tenant A does not see drives from tenant B

## Implementation Tasks

- [ ] Create `src/Strg.Core/Inbox/IInboxFolderService.cs`
- [ ] Create `src/Strg.Core/Exceptions/NoDefaultDriveException.cs`
- [ ] Create `src/Strg.Infrastructure/Inbox/InboxFolderService.cs`
- [ ] Register in DI
- [ ] Hook into upload handler to set `IsInInbox = true` on files uploaded under `/inbox`
- [ ] Write integration tests

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All TC-001 through TC-006 tests pass
- [ ] `IInboxFolderService` is in `Strg.Core` with no external NuGet dependencies
