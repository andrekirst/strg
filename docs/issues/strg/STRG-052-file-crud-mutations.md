---
id: STRG-052
title: Implement file CRUD GraphQL mutations (createFolder, deleteFile, moveFile, copyFile)
milestone: v0.1
priority: critical
status: open
type: implementation
labels: [graphql, files, api]
depends_on: [STRG-049, STRG-050, STRG-031, STRG-024]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-052: Implement file CRUD GraphQL mutations

## Summary

Implement GraphQL mutations for non-upload file operations: folder creation, deletion, move, and copy. All operations write audit log entries and fire outbox events.

## Technical Specification

### Schema:

```graphql
type Mutation {
  createFolder(driveId: UUID!, path: String!): FileItem!
  deleteFile(id: UUID!): Boolean!
  moveFile(id: UUID!, targetPath: String!, targetDriveId: UUID): FileItem!
  copyFile(id: UUID!, targetPath: String!, targetDriveId: UUID): FileItem!
  renameFile(id: UUID!, newName: String!): FileItem!
}
```

### File: `src/Strg.Core/Services/IFileService.cs`

```csharp
public interface IFileService
{
    Task<FileItem> CreateFolderAsync(Guid driveId, string path, Guid userId, CancellationToken ct);
    Task DeleteAsync(Guid fileId, Guid userId, CancellationToken ct);
    Task<FileItem> MoveAsync(Guid fileId, string targetPath, Guid? targetDriveId, Guid userId, CancellationToken ct);
    Task<FileItem> CopyAsync(Guid fileId, string targetPath, Guid? targetDriveId, Guid userId, CancellationToken ct);
    Task<FileItem> RenameAsync(Guid fileId, string newName, Guid userId, CancellationToken ct);
}
```

## Acceptance Criteria

- [ ] `createFolder` creates a folder entry in DB (no physical directory on local FS needed)
- [ ] `deleteFile` soft-deletes the `FileItem` (sets `DeletedAt`)
- [ ] `deleteFile` of a directory soft-deletes all children recursively
- [ ] `moveFile` updates `Path` and `ParentId` in DB; moves physical file in storage backend
- [ ] `copyFile` copies physical file in storage backend; creates new `FileItem` and `FileVersion`
- [ ] Move across drives is supported (`targetDriveId` optional)
- [ ] Path collision on move/copy → `409 Conflict` (target path already exists)
- [ ] All operations require `files.write` scope
- [ ] All operations create audit log entries
- [ ] All operations fire outbox events

## Test Cases

- **TC-001**: `createFolder(path: "docs/2024")` → parent folder auto-created if missing
- **TC-002**: `deleteFile` on a folder with children → all children soft-deleted
- **TC-003**: `moveFile` to existing path → `409`
- **TC-004**: `copyFile` → new `FileItem` with new ID, same content
- **TC-005**: Delete then create at same path → new file has different ID
- **TC-006**: Move to different drive → file accessible at new path/drive

## Implementation Tasks

- [ ] Create `IFileService.cs`
- [ ] Implement `FileService.cs`
- [ ] Create `FileMutations.cs`
- [ ] Implement recursive soft-delete for folders
- [ ] Add audit logging to each operation
- [ ] Fire outbox events for each operation
- [ ] Write integration tests

## Security Review Checklist

- [ ] `targetPath` validated with `StoragePath.Parse`
- [ ] User cannot move/copy files they don't own (ACL check — basic for v0.1)
- [ ] Recursive delete is bounded (prevent infinite loop on circular symlinks)

## Definition of Done

- [ ] All five mutations working in integration tests
- [ ] Audit entries verified
- [ ] Outbox events verified
