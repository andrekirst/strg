---
id: STRG-025
title: Create Drive domain entity, IDriveRepository, and drive management endpoints
milestone: v0.1
priority: critical
status: open
type: implementation
labels: [core, domain, api]
depends_on: [STRG-003, STRG-021, STRG-023]
blocks: [STRG-031, STRG-034, STRG-050]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-025: Create Drive domain entity, IDriveRepository, and drive management endpoints

## Summary

Create the `Drive` domain entity (named storage volume), `IDriveRepository`, and REST endpoints to create, list, and delete drives. A drive ties a `ProviderType` to user-visible configuration.

## Technical Specification

### File: `src/Strg.Core/Domain/Drive.cs`

```csharp
public sealed class Drive : TenantedEntity
{
    public required string Name { get; set; }
    public required string ProviderType { get; set; }
    public string ProviderConfig { get; set; } = "{}";  // JSON
    public string VersioningPolicy { get; set; } = """{"mode":"none"}""";
    public bool EncryptionEnabled { get; set; }
}
```

### REST endpoints in `src/Strg.Api/Endpoints/DriveEndpoints.cs`:

```
POST   /api/v1/drives              Create a drive (Admin)
GET    /api/v1/drives              List all drives (User - accessible ones)
GET    /api/v1/drives/{id}         Get single drive
DELETE /api/v1/drives/{id}         Delete a drive (Admin)
```

### Drive creation request:

```csharp
public record CreateDriveRequest(
    string Name,
    string ProviderType,
    JsonDocument ProviderConfig,
    string? VersioningPolicyJson = null,
    bool EncryptionEnabled = false);
```

## Acceptance Criteria

- [ ] `POST /api/v1/drives` creates a drive (requires `admin` scope)
- [ ] Drive name must be unique within tenant (URL-safe: `[a-z0-9-]`, max 64 chars)
- [ ] `ProviderType` must be registered in `IStorageProviderRegistry`
- [ ] `GET /api/v1/drives` returns only drives accessible to the current user
- [ ] `DELETE /api/v1/drives/{id}` soft-deletes the drive (not the actual files)
- [ ] Creating a duplicate drive name → 409 Conflict
- [ ] Creating a drive with unknown `ProviderType` → 422 Unprocessable Entity
- [ ] Drive `Name` is validated: lowercase, alphanumeric, hyphens only

## Test Cases

- **TC-001**: Admin creates drive `{ name: "home-nas", providerType: "local", ... }` → 201
- **TC-002**: Create drive with existing name → 409
- **TC-003**: Create drive with unknown provider type → 422
- **TC-004**: User (non-admin) creates drive → 403
- **TC-005**: List drives as user → only accessible drives returned
- **TC-006**: Delete drive → drive soft-deleted, name can be reused after deletion

## Implementation Tasks

- [ ] Create `src/Strg.Core/Domain/Drive.cs`
- [ ] Create `src/Strg.Core/Domain/IDriveRepository.cs`
- [ ] Implement `src/Strg.Infrastructure/Data/DriveRepository.cs`
- [ ] Create `src/Strg.Api/Endpoints/DriveEndpoints.cs`
- [ ] Add drive name validation (regex + length check)
- [ ] Register provider type validation in create handler
- [ ] Write integration tests for all endpoints

## Security Review Checklist

- [ ] Drive creation requires `admin` scope (not just `Authenticated`)
- [ ] `ProviderConfig` JSON is not logged (may contain credentials)
- [ ] Drive name validated to prevent path injection (no `/`, `\`, etc.)
- [ ] Deleting a drive does not delete the actual files from the storage backend (soft delete only)

## Definition of Done

- [ ] All CRUD endpoints working
- [ ] Integration tests pass
- [ ] Audit log entries created for drive create and delete
