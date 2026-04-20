---
id: STRG-052
title: Implement file CRUD GraphQL mutations (createFolder, deleteFile, moveFile, copyFile, renameFile)
milestone: v0.1
priority: critical
status: done
type: implementation
labels: [graphql, files, api]
depends_on: [STRG-049, STRG-050, STRG-031, STRG-024]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-052: Implement file CRUD GraphQL mutations

## Summary

Implement GraphQL mutations for non-upload file operations: folder creation, deletion, move, copy, and rename. All mutations live under the `storage` namespace and return Relay-style payload types. All operations write audit log entries and fire outbox events.

## Technical Specification

### Schema (under `mutation { storage { ... } }`):

```graphql
type StorageMutations {
  createFolder(input: CreateFolderInput!): CreateFolderPayload!
  deleteFile(input: DeleteFileInput!): DeleteFilePayload!
  moveFile(input: MoveFileInput!): MoveFilePayload!
  copyFile(input: CopyFileInput!): CopyFilePayload!
  renameFile(input: RenameFileInput!): RenameFilePayload!
}

type CreateFolderPayload { file: FileItem   errors: [UserError!] }
type DeleteFilePayload   { fileId: ID       errors: [UserError!] }
type MoveFilePayload     { file: FileItem   errors: [UserError!] }
type CopyFilePayload     { file: FileItem   errors: [UserError!] }
type RenameFilePayload   { file: FileItem   errors: [UserError!] }

input CreateFolderInput { driveId: ID!  path: String! }
input DeleteFileInput   { id: ID! }
input MoveFileInput     { id: ID!  targetPath: String!  targetDriveId: ID  conflictResolution: ConflictResolution }
input CopyFileInput     { id: ID!  targetPath: String!  targetDriveId: ID  conflictResolution: ConflictResolution }
input RenameFileInput   { id: ID!  newName: String! }

enum ConflictResolution { AUTO_RENAME  OVERWRITE  FAIL }
```

### File: `src/Strg.Core/Services/IFileService.cs`

```csharp
public interface IFileService
{
    Task<FileItem> CreateFolderAsync(Guid driveId, string path, Guid userId, CancellationToken ct);
    Task DeleteAsync(Guid fileId, Guid userId, CancellationToken ct);
    Task<FileItem> MoveAsync(Guid fileId, string targetPath, Guid? targetDriveId, ConflictResolution resolution, Guid userId, CancellationToken ct);
    Task<FileItem> CopyAsync(Guid fileId, string targetPath, Guid? targetDriveId, ConflictResolution resolution, Guid userId, CancellationToken ct);
    Task<FileItem> RenameAsync(Guid fileId, string newName, Guid userId, CancellationToken ct);
}
```

### File: `src/Strg.GraphQL/Mutations/FileMutations.cs`

```csharp
[ExtendObjectType<StorageMutations>]
public sealed class FileMutations
{
    [Authorize(Policy = "FilesWrite")]
    public async Task<CreateFolderPayload> CreateFolderAsync(
        CreateFolderInput input,
        [Service] IFileService fileService,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        try
        {
            var path = StoragePath.Parse(input.Path);  // throws StoragePathException if unsafe
            var folder = await fileService.CreateFolderAsync(input.DriveId, path.Value, userId, ct);
            return new CreateFolderPayload(folder, null);
        }
        catch (StoragePathException ex)
        {
            return new CreateFolderPayload(null, [new UserError("INVALID_PATH", ex.Message, "path")]);
        }
        catch (ValidationException ex)
        {
            return new CreateFolderPayload(null, [new UserError("VALIDATION_ERROR", ex.Message, ex.PropertyName)]);
        }
    }

    [Authorize(Policy = "FilesWrite")]
    public async Task<DeleteFilePayload> DeleteFileAsync(
        DeleteFileInput input,
        [Service] IFileService fileService,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        try
        {
            await fileService.DeleteAsync(input.Id, userId, ct);
            return new DeleteFilePayload(input.Id, null);
        }
        catch (NotFoundException ex)
        {
            return new DeleteFilePayload(null, [new UserError("NOT_FOUND", ex.Message, null)]);
        }
    }

    // moveFile, copyFile, renameFile follow the same pattern
}
```

## Acceptance Criteria

- [ ] `mutation { storage { createFolder(input: { driveId: "...", path: "docs/2024" }) { file { id name isFolder } errors { code field } } } }` → folder created
- [ ] `createFolder` with unsafe path → `errors: [{ code: "INVALID_PATH", field: "path" }]`
- [ ] `deleteFile` soft-deletes the `FileItem` (sets `DeletedAt`)
- [ ] `deleteFile` of a folder soft-deletes all children recursively
- [ ] `moveFile` with `conflictResolution: AUTO_RENAME` → new name generated if collision
- [ ] `copyFile` → new `FileItem` with new ID, same content
- [ ] Move across drives supported via optional `targetDriveId`
- [ ] All operations require `files.write` scope
- [ ] All operations create audit log entries
- [ ] All operations fire outbox events

## Test Cases

- **TC-001**: `createFolder(path: "docs/2024")` → parent folder auto-created if missing
- **TC-002**: `deleteFile` on a folder with children → all children soft-deleted
- **TC-003**: `moveFile` to existing path with `FAIL` resolution → `errors: [{ code: "CONFLICT" }]`
- **TC-004**: `copyFile` → new `FileItem` with new ID, same content
- **TC-005**: Delete then create at same path → new file has different ID
- **TC-006**: Move to different drive → file accessible at new path/drive
- **TC-007**: `createFolder` with path traversal attempt → `errors: [{ code: "INVALID_PATH" }]`

## Implementation Tasks

- [ ] Create `IFileService.cs` in `Strg.Core/Services/`
- [ ] Implement `FileService.cs` in `Strg.Infrastructure/`
- [ ] Create `FileMutations.cs` with `[ExtendObjectType<StorageMutations>]`
- [ ] Create payload records in `src/Strg.GraphQL/Payloads/`
- [ ] Create input records in `src/Strg.GraphQL/Inputs/`
- [ ] Implement recursive soft-delete for folders
- [ ] Add audit logging to each operation
- [ ] Fire outbox events for each operation
- [ ] Types are auto-discovered by `AddTypes()` — no manual registration

## Security Review Checklist

- [ ] All user-supplied paths validated with `StoragePath.Parse` before reaching `IStorageProvider`
- [ ] User cannot move/copy files they don't own (ACL check)
- [ ] Recursive delete bounded (prevent performance issues on deep trees)
- [ ] `ConflictResolution.Fail` returns typed error, never throws unhandled exception

## Definition of Done

- [ ] All five mutations working with payload pattern in integration tests
- [ ] Audit entries verified
- [ ] Outbox events verified
