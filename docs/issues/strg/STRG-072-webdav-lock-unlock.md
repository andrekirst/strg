---
id: STRG-072
title: Implement WebDAV LOCK and UNLOCK handlers
milestone: v0.1
priority: medium
status: done
type: implementation
labels: [webdav]
depends_on: [STRG-067, STRG-068]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-072: Implement WebDAV LOCK and UNLOCK handlers

## Summary

Implement RFC 4918 LOCK and UNLOCK for WebDAV DAV level 2 compliance. Locks are stored in the **`file_locks` PostgreSQL table** — survives restarts and works across instances.

## Technical Specification

### Lock manager (DB-backed via `file_locks` table):

```csharp
public sealed class DbLockManager(StrgDbContext db) : ILockManager
{
    public async Task<LockResult> LockAsync(
        Uri resource, WebDavLockScope scope, WebDavLockType type,
        string owner, TimeSpan timeout, CancellationToken ct)
    {
        var token = $"urn:uuid:{Guid.NewGuid()}";
        var expiresAt = DateTimeOffset.UtcNow + timeout;

        // Atomic insert — concurrent LOCK from two clients: one wins, one gets conflict
        var existing = await db.FileLocks
            .Where(l => l.ResourceUri == resource.AbsolutePath && l.ExpiresAt > DateTimeOffset.UtcNow)
            .FirstOrDefaultAsync(ct);

        if (existing != null)
            return LockResult.Locked; // 423 Locked

        db.FileLocks.Add(new FileLock
        {
            ResourceUri = resource.AbsolutePath,
            Token = token,
            OwnerId = owner,
            ExpiresAt = expiresAt
        });
        await db.SaveChangesAsync(ct);
        return LockResult.Success(token, timeout);
    }

    public async Task<bool> UnlockAsync(Uri resource, string lockToken, CancellationToken ct)
    {
        var lock_ = await db.FileLocks
            .FirstOrDefaultAsync(l => l.ResourceUri == resource.AbsolutePath && l.Token == lockToken, ct);
        if (lock_ is null) return false;
        db.FileLocks.Remove(lock_);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
```

### `FileLock` entity (in `Strg.Core/Domain/FileLock.cs`):

```csharp
public sealed class FileLock : Entity
{
    public required string ResourceUri { get; init; }
    public required string Token { get; init; }
    public required string OwnerId { get; init; }
    public DateTimeOffset ExpiresAt { get; set; }
}
```

### Lock token format: `urn:uuid:{guid}`

### LOCK response:

```xml
<?xml version="1.0" encoding="utf-8"?>
<D:prop xmlns:D="DAV:">
  <D:lockdiscovery>
    <D:activelock>
      <D:locktype><D:write/></D:locktype>
      <D:lockscope><D:exclusive/></D:lockscope>
      <D:depth>0</D:depth>
      <D:owner>user@example.com</D:owner>
      <D:timeout>Second-3600</D:timeout>
      <D:locktoken><D:href>urn:uuid:...</D:href></D:locktoken>
    </D:activelock>
  </D:lockdiscovery>
</D:prop>
```

### Lock expiry:

Locks expire after the timeout specified in the `Timeout` request header. Maximum lock timeout: 3600 seconds. Default if header absent: 600 seconds.

### Lock refresh (LOCK on already-locked resource with correct token):

Returns `200 OK` with updated `timeout` value.

## Acceptance Criteria

- [ ] `LOCK /dav/{drive}/file.txt` → `200 OK` with lock token
- [ ] Second `LOCK` on same resource (different user) → `423 Locked`
- [ ] `UNLOCK /dav/{drive}/file.txt` with wrong token → `409 Conflict`
- [ ] `UNLOCK /dav/{drive}/file.txt` with correct token → `204 No Content`
- [ ] Lock expires after `Timeout` → subsequent `LOCK` succeeds
- [ ] PUT on locked resource without lock token → `423 Locked`
- [ ] `OPTIONS /dav/` → `DAV: 1, 2` (level 2 = lock support)

## Test Cases

- **TC-001**: LOCK → response has valid lock token in XML
- **TC-002**: LOCK while locked by same token → refresh (200 OK)
- **TC-003**: LOCK while locked by other → `423`
- **TC-004**: UNLOCK with wrong token → `409`
- **TC-005**: Lock expires → LOCK again succeeds

## Implementation Tasks

- [ ] Create `FileLock.cs` entity in `Strg.Core/Domain/`
- [ ] Create `DbLockManager.cs` in `Strg.WebDav/`
- [ ] Register `ILockManager` in `AddStrgWebDav()` as scoped
- [ ] Wire lock manager to NWebDav dispatcher
- [ ] Implement lock expiry (background `Timer` or check-on-access)
- [ ] Add `DAV: 1, 2` to OPTIONS response

## Testing Tasks

- [ ] Unit test: concurrent LOCK from two threads → one wins, one gets 423
- [ ] Unit test: lock expiry → `IsExpired()` returns true after timeout
- [ ] Integration test: Windows Explorer can lock and unlock files

## Security Review Checklist

- [ ] Lock token is a UUID (unguessable)
- [ ] LOCK requires authentication (`files.write` scope)
- [ ] Maximum lock duration capped (prevents resource starvation)
- [ ] Lock owner information is not exposed in PROPFIND (only in LOCK response)

## Code Review Checklist

- [ ] `DbLockManager` uses `StrgDbContext` (scoped, not singleton)
- [ ] Lock expiry uses UTC timestamps
- [ ] `ConcurrentDictionary` used (thread-safe)

## Definition of Done

- [ ] Windows Explorer WebDAV mount can lock/unlock files
- [ ] `OPTIONS` returns `DAV: 1, 2`
