---
id: STRG-324
title: Copy action
milestone: v0.2
priority: low
status: open
type: implementation
labels: [inbox, actions, copy]
depends_on: [STRG-308, STRG-032]
blocks: []
assigned_agent_type: feature-dev
estimated_complexity: small
---

# STRG-324: Copy action

## Summary

Implement the `CopyAction` inbox action type. When a rule has only a `CopyAction`, the original file stays in the inbox with status `Processed` (a copy is placed at the target). When a rule has both `CopyAction` and `MoveAction`, copy executes first (preserving the original in inbox briefly), then move executes (removing original from inbox). Quota is debited from the destination drive owner for the copy.

## Technical Specification

### New action type (`src/Strg.Core/Domain/Inbox/InboxAction.cs`)

```csharp
[JsonDerivedType(typeof(CopyAction), "copy")]
```

```csharp
public record CopyAction(
    string TargetPath,
    Guid? TargetDriveId = null,
    ConflictResolution ConflictResolution = ConflictResolution.AutoRename,
    bool AutoCreateFolders = true
) : InboxAction(ConflictResolution, AutoCreateFolders);
```

### Action execution (in `InboxProcessingConsumer`)

Add case to `ExecuteActionAsync`:

```csharp
if (action is CopyAction copy)
{
    var targetDriveId = copy.TargetDriveId ?? file.DriveId;
    var targetPath = StoragePath.Parse(copy.TargetPath).Value;

    if (copy.AutoCreateFolders)
        await EnsurePathAsync(targetDriveId, targetPath, file.TenantId, file.CreatedBy, ct);

    // Conflict resolution (same logic as MoveAction)
    var finalName = await ResolveConflictAsync(targetDriveId, targetPath, file.Name, copy.ConflictResolution, ct);

    // Create a new FileItem pointing to the same storage content
    var copyItem = new FileItem
    {
        DriveId = targetDriveId,
        TenantId = file.TenantId,
        Name = finalName,
        Path = $"{targetPath}/{finalName}",
        Size = file.Size,
        ContentHash = file.ContentHash,
        MimeType = file.MimeType,
        IsDirectory = false,
        CreatedBy = file.CreatedBy
    };
    db.Files.Add(copyItem);

    // Copy the physical file in the storage provider
    var sourceProvider = _registry.Get(file.ProviderType);
    await sourceProvider.CopyAsync(StoragePath.Parse(file.Path).Value,
                                   StoragePath.Parse(copyItem.Path).Value, ct);

    // Quota: charge destination
    await _quota.CommitAsync(file.CreatedBy, file.Size, ct);

    await db.SaveChangesAsync(ct);
}
```

### Post-execution inbox status rules

After all actions in a rule are executed:

- Rule has **only** `CopyAction` (no `MoveAction`): original file stays in inbox, `IsInInbox = true`, status = `Processed`. File is "processed" even though it's still in inbox.
- Rule has **both** `CopyAction` and `MoveAction`: copy executes first, then move. After move, `IsInInbox = false` as usual.

This logic is enforced in `InboxProcessingConsumer.Consume` after the actions loop:

```csharp
var hasMoveAction = actions.OfType<MoveAction>().Any();
if (finalStatus == InboxFileStatus.Processed && !hasMoveAction)
    file.IsInInbox = true; // copy-only: stay in inbox with Processed status
```

### GraphQL input type update

```graphql
input CopyActionInput {
  targetPath: String!
  targetDriveId: ID
  conflictResolution: ConflictResolution = AUTO_RENAME
  autoCreateFolders: Boolean = true
}
```

## Acceptance Criteria

- [ ] `CopyAction` record exists in `Strg.Core` with `[JsonDerivedType]`
- [ ] `InboxProcessingConsumer` handles `CopyAction` in `ExecuteActionAsync`
- [ ] Copy-only rule: original file stays in inbox with `IsInInbox = true` and status `Processed`
- [ ] Copy + Move rule: copy executes first, then move; original leaves inbox
- [ ] Quota charged on destination drive for the copy
- [ ] `CopyActionInput` exposed in GraphQL mutations
- [ ] All path inputs go through `StoragePath.Parse()`

## Test Cases

- TC-001: Copy-only rule → copy at target path; original stays in inbox; status = Processed
- TC-002: Copy + Move rule → copy at target; original at move target; original leaves inbox
- TC-003: Cross-drive copy → destination drive quota debited
- TC-004: Copy with `AutoRename` conflict strategy → unique name generated at target
- TC-005: Copy to non-existent folder with `AutoCreateFolders = true` → folder created

## Implementation Tasks

- [ ] Add `CopyAction` to `InboxAction.cs`
- [ ] Add `ExecuteActionAsync` case for `CopyAction` in `InboxProcessingConsumer`
- [ ] Implement `IStorageProvider.CopyAsync` if not already present (or use read+write fallback)
- [ ] Update post-action status logic in consumer
- [ ] Add `CopyActionInput` GraphQL input type
- [ ] Write integration tests

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All TC-001 through TC-005 tests pass
