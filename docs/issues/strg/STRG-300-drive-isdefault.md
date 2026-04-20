---
id: STRG-300
title: Drive entity — add IsDefault field
milestone: v0.1
priority: high
status: open
type: implementation
labels: [domain, storage, inbox]
depends_on: [STRG-025]
blocks: [STRG-307]
assigned_agent_type: feature-dev
estimated_complexity: small
---

# STRG-300: Drive entity — add IsDefault field

## Summary

Add an `IsDefault` boolean field to the `Drive` entity so the inbox system can identify which drive to place the user's `/inbox` folder on. Only one drive per user (within the same tenant) may be `IsDefault = true` at any given time. The constraint is enforced at the service layer, not via a unique DB index, because toggling the default drive requires swapping two rows atomically.

## Technical Specification

### Domain change (`src/Strg.Core/Domain/Drive.cs`)

```csharp
public sealed class Drive : TenantedEntity
{
    public required string Name { get; set; }
    public required string ProviderType { get; set; }
    public string ProviderConfig { get; set; } = "{}";
    public string VersioningPolicy { get; set; } = /*...existing...*/;
    public bool EncryptionEnabled { get; set; }
    public bool IsDefault { get; set; }  // NEW
}
```

### Service behaviour (`src/Strg.Infrastructure/Services/DriveService.cs`)

When a user creates their **first** drive, `IsDefault` is set to `true` automatically.

When a user calls `setDefaultDrive(driveId)`, the service:
1. Loads the current default drive (if any) and sets `IsDefault = false`.
2. Sets `IsDefault = true` on the target drive.
3. Calls `SaveChangesAsync` once — both changes commit atomically.

```csharp
public interface IDriveService
{
    Task SetDefaultDriveAsync(Guid driveId, Guid userId, CancellationToken ct = default);
    Task<Drive?> GetDefaultDriveAsync(Guid userId, CancellationToken ct = default);
}
```

### EF Core configuration (`src/Strg.Infrastructure/Persistence/Configurations/DriveConfiguration.cs`)

Add column config for `IsDefault` (no unique index — enforced by service):

```csharp
builder.Property(d => d.IsDefault).HasDefaultValue(false);
```

### GraphQL (`src/Strg.GraphQL/Types/DriveType.cs` + `src/Strg.GraphQL/Mutations/DriveMutations.cs`)

- Expose `isDefault: Boolean!` field on the `Drive` GraphQL type.
- Add mutation: `setDefaultDrive(driveId: ID!): SetDefaultDrivePayload!`

### Migration

New migration: `AddDriveIsDefault` that adds the `is_default` column (nullable → `false` default).

## Acceptance Criteria

- [ ] `Drive.IsDefault` property exists and defaults to `false`
- [ ] First drive created by a user automatically has `IsDefault = true`
- [ ] `IDriveService.SetDefaultDriveAsync` atomically swaps the default flag
- [ ] `IDriveService.GetDefaultDriveAsync` returns the user's current default drive (or `null`)
- [ ] EF Core migration `AddDriveIsDefault` creates the `is_default` column
- [ ] `setDefaultDrive` GraphQL mutation is exposed and protected by auth
- [ ] `isDefault` field is exposed on the GraphQL `Drive` type

## Test Cases

- TC-001: Create first drive → `IsDefault` is automatically `true`
- TC-002: Create second drive → `IsDefault` is `false`; first drive stays `true`
- TC-003: `SetDefaultDriveAsync` swaps flags correctly; at most one drive is default after call
- TC-004: `GetDefaultDriveAsync` returns `null` when user has no drives
- TC-005: Attempt to set default drive belonging to a different tenant → throws `NotFoundException`

## Implementation Tasks

- [ ] Add `IsDefault` property to `Drive.cs`
- [ ] Update `DriveConfiguration.cs` with column mapping
- [ ] Add `IDriveService` interface and `DriveService` implementation to `Strg.Infrastructure`
- [ ] Update `DriveService` so first-drive creation sets `IsDefault = true`
- [ ] Register `IDriveService` in DI
- [ ] Create EF Core migration `AddDriveIsDefault`
- [ ] Expose `isDefault` field in GraphQL `DriveType`
- [ ] Add `setDefaultDrive` mutation
- [ ] Write unit + integration tests

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All TC-001 through TC-005 tests pass
- [ ] Migration applies cleanly on a fresh database
- [ ] Existing Drive tests are not regressed
