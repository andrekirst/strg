---
id: STRG-060
title: Implement drive REST endpoints (GET /drives, GET /drives/{id})
milestone: v0.1
priority: high
status: open
type: implementation
labels: [api, drives, rest]
depends_on: [STRG-025, STRG-013]
blocks: [STRG-034, STRG-037, STRG-038]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-060: Implement drive REST endpoints

## Summary

Implement REST endpoints for drive discovery and management. While drives can be managed via GraphQL mutations (STRG-053, STRG-057), the REST endpoints are needed for TUS upload metadata, file download, and tooling that prefers REST over GraphQL.

## Technical Specification

### Routes:

```
GET    /api/v1/drives              → list all drives for the tenant
GET    /api/v1/drives/{driveId}    → single drive details
POST   /api/v1/drives              [Admin] → create drive
DELETE /api/v1/drives/{driveId}    [Admin] → soft-delete drive
```

### DriveDto (never exposes `ProviderConfig`):

```csharp
public record DriveDto(
    Guid Id,
    string Name,
    string ProviderType,
    bool EncryptionEnabled,
    DateTimeOffset CreatedAt)
{
    public static DriveDto From(Drive d) => new(
        d.Id, d.Name, d.ProviderType, d.EncryptionEnabled, d.CreatedAt);
}
```

### File: `src/Strg.Api/Endpoints/DriveEndpoints.cs`

```csharp
public static class DriveEndpoints
{
    public static IEndpointRouteBuilder MapDriveEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/drives")
            .RequireAuthorization("FilesRead");

        group.MapGet("/", ListDrivesAsync);
        group.MapGet("/{driveId:guid}", GetDriveAsync);
        group.MapPost("/", CreateDriveAsync).RequireAuthorization("Admin");
        group.MapDelete("/{driveId:guid}", DeleteDriveAsync).RequireAuthorization("Admin");

        return routes;
    }

    private static async Task<IResult> ListDrivesAsync(
        [FromServices] StrgDbContext db,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var tenantId = user.GetTenantId();
        var drives = await db.Drives
            .Where(d => d.TenantId == tenantId)
            .OrderBy(d => d.Name)
            .Select(d => DriveDto.From(d))
            .ToListAsync(ct);
        return Results.Ok(drives);
    }
}
```

### CreateDriveRequest:

```json
{
  "name": "my-documents",
  "providerType": "local",
  "providerConfig": {},
  "encryptionEnabled": false
}
```

Drive name validation: `^[a-z0-9][a-z0-9-]{0,63}$`

## Acceptance Criteria

- [ ] `GET /api/v1/drives` → list of all tenant drives (without `providerConfig`)
- [ ] `GET /api/v1/drives/{driveId}` → single drive or `404`
- [ ] `POST /api/v1/drives` (Admin) → drive created, `201 Created`
- [ ] `DELETE /api/v1/drives/{driveId}` (Admin) → soft-deleted, `204 No Content`
- [ ] Drive name not matching `[a-z0-9-]` → `400 Bad Request`
- [ ] Duplicate drive name → `409 Conflict`
- [ ] `DriveDto` never contains `ProviderConfig`

## Test Cases

- **TC-001**: `GET /drives` → only current tenant's drives
- **TC-002**: `GET /drives/{wrongTenantDriveId}` → `404`
- **TC-003**: `POST /drives` non-admin → `403`
- **TC-004**: `POST /drives` with invalid name → `400`
- **TC-005**: `DELETE /drives/{id}` → `204`, drive absent from subsequent `GET`

## Implementation Tasks

- [ ] Create `DriveEndpoints.cs` in `Strg.Api/Endpoints/`
- [ ] Create `DriveDto` record
- [ ] Create `CreateDriveRequest` and its validator
- [ ] Register endpoints in `Program.cs`

## Testing Tasks

- [ ] Integration test: create + list + delete drive lifecycle

## Security Review Checklist

- [ ] `ProviderConfig` not in `DriveDto` (may contain credentials)
- [ ] `TenantId` not in `DriveDto`
- [ ] Admin policy required for create/delete

## Code Review Checklist

- [ ] Drive name validated via FluentValidation (STRG-085)
- [ ] Soft-delete only (no physical storage deletion on drive delete)

## Definition of Done

- [ ] All 4 endpoints working
- [ ] `providerConfig` absent from responses confirmed
