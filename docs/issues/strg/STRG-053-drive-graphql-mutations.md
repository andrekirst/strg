---
id: STRG-053
title: Implement Drive GraphQL mutations (createDrive, updateDrive, deleteDrive)
milestone: v0.1
priority: high
status: open
type: implementation
labels: [graphql, drives, api]
depends_on: [STRG-049, STRG-050, STRG-025]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-053: Implement Drive GraphQL mutations

## Summary

Implement GraphQL mutations for drive lifecycle management: create, update, and delete. Drive creation validates the name format and storage provider configuration. Only admins can create or delete drives.

## Technical Specification

### Schema:

```graphql
type Mutation {
  createDrive(input: CreateDriveInput!): Drive!
  updateDrive(id: UUID!, input: UpdateDriveInput!): Drive!
  deleteDrive(id: UUID!): Boolean!
}

input CreateDriveInput {
  name: String!
  providerType: String!
  providerConfig: JSON
  encryptionEnabled: Boolean!
}

input UpdateDriveInput {
  name: String
  encryptionEnabled: Boolean
}
```

### File: `src/Strg.GraphQL/Mutations/DriveMutations.cs`

```csharp
[ExtendObjectType("Mutation")]
public class DriveMutations
{
    [Authorize(Policy = "Admin")]
    [Error(typeof(ValidationException))]
    [Error(typeof(DuplicateDriveNameException))]
    public async Task<Drive> CreateDrive(
        CreateDriveInput input,
        [Service] StrgDbContext db,
        [Service] IStorageProviderRegistry registry,
        [GlobalState("tenantId")] Guid tenantId,
        CancellationToken ct)
    {
        // Validate drive name format
        if (!DriveNameValidator.IsValid(input.Name))
            throw new ValidationException("Drive name must match [a-z0-9-], max 64 chars.");

        if (await db.Drives.AnyAsync(d => d.TenantId == tenantId && d.Name == input.Name, ct))
            throw new DuplicateDriveNameException(input.Name);

        // Validate provider type exists
        registry.ValidateProviderType(input.ProviderType);

        var drive = new Drive
        {
            TenantId = tenantId,
            Name = input.Name,
            ProviderType = input.ProviderType,
            ProviderConfig = input.ProviderConfig?.ToString() ?? "{}",
            EncryptionEnabled = input.EncryptionEnabled
        };

        db.Drives.Add(drive);
        await db.SaveChangesAsync(ct);
        return drive;
    }

    [Authorize(Policy = "Admin")]
    public async Task<bool> DeleteDrive(
        Guid id,
        [Service] StrgDbContext db,
        [GlobalState("tenantId")] Guid tenantId,
        CancellationToken ct)
    {
        var drive = await db.Drives.FirstOrDefaultAsync(
            d => d.Id == id && d.TenantId == tenantId, ct);
        if (drive is null) return false;

        drive.DeletedAt = DateTimeOffset.UtcNow;
        drive.IsDeleted = true;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
```

## Acceptance Criteria

- [ ] `mutation { createDrive(input: { name: "my-drive", providerType: "local", encryptionEnabled: false }) { id } }` → drive created
- [ ] Drive name not matching `[a-z0-9-]` → validation error
- [ ] Duplicate drive name → `DuplicateDriveNameException` GraphQL error
- [ ] Unknown `providerType` → validation error
- [ ] `deleteDrive` soft-deletes the drive
- [ ] Non-admin user → `UNAUTHORIZED` GraphQL error
- [ ] Deleted drive's files still accessible via version history (files not deleted)

## Test Cases

- **TC-001**: Create drive → appears in `query { drives { id name } }`
- **TC-002**: Duplicate drive name → error with code `DUPLICATE_DRIVE_NAME`
- **TC-003**: Delete drive → no longer appears in drive list
- **TC-004**: Non-admin `createDrive` → `UNAUTHORIZED`
- **TC-005**: Drive name `"My Drive"` (uppercase + space) → validation error

## Implementation Tasks

- [ ] Create `DriveMutations.cs` in `Strg.GraphQL/Mutations/`
- [ ] Create `CreateDriveInput` and `UpdateDriveInput` records
- [ ] Create `DuplicateDriveNameException`
- [ ] Create `DriveNameValidator` (regex: `^[a-z0-9][a-z0-9-]{0,63}$`)
- [ ] Register type in Hot Chocolate setup (STRG-049)

## Testing Tasks

- [ ] Integration test: create drive → query returns drive
- [ ] Integration test: duplicate name → error

## Security Review Checklist

- [ ] `providerConfig` not echoed back in mutation response (may contain credentials)
- [ ] Admin policy required for create/delete
- [ ] Drive name validated to prevent path injection

## Code Review Checklist

- [ ] `DriveType.ProviderConfig` is already ignored in STRG-050 (credentials not exposed)
- [ ] Tenant ID comes from JWT, not from mutation input

## Definition of Done

- [ ] Create and delete mutations working
- [ ] Name validation enforced
