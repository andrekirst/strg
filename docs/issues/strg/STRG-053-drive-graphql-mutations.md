---
id: STRG-053
title: Implement Drive GraphQL mutations (createDrive, updateDrive, deleteDrive)
milestone: v0.1
priority: high
status: done
type: implementation
labels: [graphql, drives, api]
depends_on: [STRG-049, STRG-050, STRG-025]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-053: Implement Drive GraphQL mutations

## Summary

Implement GraphQL mutations for drive lifecycle management: create, update, and delete. All mutations live under the `storage` namespace and return Relay-style payload types. Only admins can create or delete drives.

## Technical Specification

### Schema (under `mutation { storage { ... } }`):

```graphql
type StorageMutations {
  createDrive(input: CreateDriveInput!): CreateDrivePayload!
  updateDrive(input: UpdateDriveInput!): UpdateDrivePayload!
  deleteDrive(input: DeleteDriveInput!): DeleteDrivePayload!
}

type CreateDrivePayload { drive: Drive  errors: [UserError!] }
type UpdateDrivePayload { drive: Drive  errors: [UserError!] }
type DeleteDrivePayload { driveId: ID   errors: [UserError!] }

input CreateDriveInput {
  name: String!
  providerType: String!
  providerConfig: JSON!   # validated server-side; never returned in any response
  isDefault: Boolean
  isEncrypted: Boolean
}

input UpdateDriveInput {
  id: ID!
  name: String
  isDefault: Boolean
}

input DeleteDriveInput { id: ID! }
```

### File: `src/Strg.GraphQL/Mutations/DriveMutations.cs`

```csharp
[ExtendObjectType<StorageMutations>]
public sealed class DriveMutations
{
    [Authorize(Policy = "Admin")]
    public async Task<CreateDrivePayload> CreateDriveAsync(
        CreateDriveInput input,
        [Service] StrgDbContext db,
        [Service] IStorageProviderRegistry registry,
        [GlobalState("tenantId")] Guid tenantId,
        CancellationToken ct)
    {
        if (!DriveNameValidator.IsValid(input.Name))
            return new CreateDrivePayload(null, [new UserError("VALIDATION_ERROR",
                "Drive name must match [a-z0-9-], max 64 chars.", "name")]);

        if (await db.Drives.AnyAsync(d => d.TenantId == tenantId && d.Name == input.Name, ct))
            return new CreateDrivePayload(null, [new UserError("DUPLICATE_DRIVE_NAME",
                $"Drive '{input.Name}' already exists.", "name")]);

        try { registry.ValidateProviderType(input.ProviderType); }
        catch (ValidationException ex)
        {
            return new CreateDrivePayload(null, [new UserError("VALIDATION_ERROR", ex.Message, "providerType")]);
        }

        var drive = new Drive
        {
            TenantId = tenantId,
            Name = input.Name,
            ProviderType = input.ProviderType,
            ProviderConfig = input.ProviderConfig.ToString(),  // stored, never returned
            IsEncrypted = input.IsEncrypted ?? false,
            IsDefault = input.IsDefault ?? false
        };

        db.Drives.Add(drive);
        await db.SaveChangesAsync(ct);
        return new CreateDrivePayload(drive, null);
    }

    [Authorize(Policy = "Admin")]
    public async Task<DeleteDrivePayload> DeleteDriveAsync(
        DeleteDriveInput input,
        [Service] StrgDbContext db,
        [GlobalState("tenantId")] Guid tenantId,
        CancellationToken ct)
    {
        var drive = await db.Drives.FirstOrDefaultAsync(
            d => d.Id == (Guid)input.Id && d.TenantId == tenantId, ct);

        if (drive is null)
            return new DeleteDrivePayload(null, [new UserError("NOT_FOUND", "Drive not found.", null)]);

        drive.DeletedAt = DateTimeOffset.UtcNow;
        drive.IsDeleted = true;
        await db.SaveChangesAsync(ct);
        return new DeleteDrivePayload(input.Id, null);
    }
}
```

## Acceptance Criteria

- [ ] `mutation { storage { createDrive(input: { name: "my-drive", providerType: "local", providerConfig: {}, isEncrypted: false }) { drive { id name } errors { code field } } } }` → drive created
- [ ] Drive name not matching `[a-z0-9-]` → `errors: [{ code: "VALIDATION_ERROR", field: "name" }]`
- [ ] Duplicate drive name → `errors: [{ code: "DUPLICATE_DRIVE_NAME", field: "name" }]`
- [ ] Unknown `providerType` → `errors: [{ code: "VALIDATION_ERROR", field: "providerType" }]`
- [ ] `deleteDrive` soft-deletes; drive no longer appears in queries
- [ ] Non-admin user → `UNAUTHORIZED` (HC authorization rejects before mutation runs)
- [ ] `providerConfig` never returned in any payload or query response
- [ ] `TenantId` comes from JWT, never from mutation input

## Test Cases

- **TC-001**: Create drive → appears in `query { storage { drives { nodes { id name } } } }`
- **TC-002**: Duplicate drive name → `errors[0].code = "DUPLICATE_DRIVE_NAME"`
- **TC-003**: Delete drive → no longer appears in drive list
- **TC-004**: Non-admin `createDrive` → HTTP 200 with `UNAUTHORIZED` in errors
- **TC-005**: Drive name `"My Drive"` (uppercase + space) → `errors[0].field = "name"`

## Implementation Tasks

- [ ] Create `DriveMutations.cs` in `src/Strg.GraphQL/Mutations/` with `[ExtendObjectType<StorageMutations>]`
- [ ] Create payload records `CreateDrivePayload`, `UpdateDrivePayload`, `DeleteDrivePayload` in `src/Strg.GraphQL/Payloads/`
- [ ] Create input records `CreateDriveInput`, `UpdateDriveInput`, `DeleteDriveInput` in `src/Strg.GraphQL/Inputs/`
- [ ] Create `DriveNameValidator` (regex `^[a-z0-9][a-z0-9-]{0,63}$`)
- [ ] Types are auto-discovered by `AddTypes()` — no manual registration

## Security Review Checklist

- [ ] `providerConfig` never echoed back in mutation response or any query
- [ ] Admin policy required for create/delete (enforced via `[Authorize(Policy = "Admin")]`)
- [ ] Drive name validated to prevent path injection
- [ ] `TenantId` sourced from JWT `[GlobalState("tenantId")]`, never from input

## Definition of Done

- [ ] Create, update, and delete mutations working with payload pattern
- [ ] Name validation enforced with typed errors
