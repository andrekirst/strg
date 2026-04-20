---
id: STRG-044
title: Implement REST file versions endpoint (GET/POST /files/{fileId}/versions)
milestone: v0.1
priority: medium
status: open
type: implementation
labels: [api, files, versioning, rest]
depends_on: [STRG-043, STRG-033]
blocks: [STRG-045]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-044: Implement REST file versions endpoint

## Summary

Implement REST endpoints for listing file versions and downloading a specific version. Version restore is covered in STRG-045.

## Technical Specification

### Routes:

```
GET    /api/v1/drives/{driveId}/files/{fileId}/versions
GET    /api/v1/drives/{driveId}/files/{fileId}/versions/{versionNumber}/content
```

### List versions response:

```json
[
  {
    "versionNumber": 3,
    "size": 1024000,
    "contentHash": "sha256:abc123",
    "createdAt": "2024-01-03T00:00:00Z",
    "createdBy": "uuid"
  },
  {
    "versionNumber": 2,
    "size": 980000,
    "contentHash": "sha256:def456",
    "createdAt": "2024-01-02T00:00:00Z",
    "createdBy": "uuid"
  }
]
```

### Download version content:

```csharp
private static async Task<IResult> GetVersionContentAsync(
    Guid driveId,
    Guid fileId,
    int versionNumber,
    [FromServices] IFileVersionRepository versionRepo,
    [FromServices] IFileRepository fileRepo,
    [FromServices] IStorageProviderRegistry registry,
    CancellationToken ct)
{
    var file = await fileRepo.GetByIdAsync(fileId, ct);
    if (file is null || file.DriveId != driveId) return Results.NotFound();

    var version = await versionRepo.GetVersionAsync(fileId, versionNumber, ct);
    if (version is null) return Results.NotFound();

    var drive = /* ... */;
    var provider = registry.GetProvider(drive.ProviderType, drive.ProviderConfig);
    var stream = await provider.ReadAsync(version.StorageKey, ct);

    return Results.File(
        stream,
        contentType: file.MimeType,
        fileDownloadName: $"{file.Name}.v{versionNumber}",
        enableRangeProcessing: true);
}
```

## Acceptance Criteria

- [ ] `GET .../versions` → list of all versions, descending by `versionNumber`
- [ ] `GET .../versions/{n}/content` → `200` with version content stream
- [ ] `GET .../versions/999/content` where version doesn't exist → `404`
- [ ] Requires `files.read` scope
- [ ] Range requests supported on version content download (`206 Partial Content`)

## Test Cases

- **TC-001**: Upload 3 times → list versions → 3 entries
- **TC-002**: GET version 1 content → bytes match first upload
- **TC-003**: GET nonexistent version → 404

## Implementation Tasks

- [ ] Add `MapGet("{fileId}/versions")` in `FileEndpoints.cs`
- [ ] Add `MapGet("{fileId}/versions/{versionNumber}/content")` in `FileEndpoints.cs`
- [ ] Create `FileVersionDto` record
- [ ] Enable range processing on version download

## Testing Tasks

- [ ] Integration test: upload twice → version 1 and version 2 downloadable
- [ ] Integration test: version 1 content still accessible after version 2 uploaded

## Security Review Checklist

- [ ] Requires auth (files.read scope)
- [ ] Version storage key not exposed in API response

## Code Review Checklist

- [ ] Version list is ordered descending (latest first)
- [ ] Download uses `Results.File` with `enableRangeProcessing: true`

## Definition of Done

- [ ] Version list and download work
- [ ] Integration test passes
