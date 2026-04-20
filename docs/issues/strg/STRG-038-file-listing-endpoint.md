---
id: STRG-038
title: Implement REST file listing endpoint (GET /drives/{driveId}/files)
milestone: v0.1
priority: high
status: open
type: implementation
labels: [api, files, rest]
depends_on: [STRG-033, STRG-025, STRG-013]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-038: Implement REST file listing endpoint

## Summary

Implement `GET /api/v1/drives/{driveId}/files` that returns a paginated list of `FileItem` records in a drive. Supports filtering by path prefix and recursive listing. Complements the GraphQL queries in STRG-050.

## Technical Specification

### Route: `GET /api/v1/drives/{driveId}/files`

### Query parameters:

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | string | `/` | Directory path to list |
| `recursive` | bool | `false` | Include all descendants |
| `page` | int | `1` | Page number |
| `pageSize` | int | `50` | Items per page (max 200) |

### Response:

```json
{
  "items": [
    {
      "id": "uuid",
      "name": "report.pdf",
      "path": "docs/report.pdf",
      "size": 1024000,
      "mimeType": "application/pdf",
      "isDirectory": false,
      "contentHash": "sha256:abc123",
      "createdAt": "2024-01-01T00:00:00Z",
      "updatedAt": "2024-01-01T00:00:00Z"
    }
  ],
  "page": 1,
  "pageSize": 50,
  "totalCount": 142
}
```

### File: `src/Strg.Api/Endpoints/FileEndpoints.cs`:

```csharp
public static class FileEndpoints
{
    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/drives/{driveId}/files")
            .RequireAuthorization("FilesRead");

        group.MapGet("/", ListFilesAsync);
        // ... other endpoints

        return routes;
    }

    private static async Task<IResult> ListFilesAsync(
        Guid driveId,
        string path = "/",
        bool recursive = false,
        int page = 1,
        int pageSize = 50,
        [FromServices] IFileRepository repo = null!,
        [FromServices] IDriveRepository driveRepo = null!,
        ClaimsPrincipal user = null!,
        CancellationToken ct = default)
    {
        if (pageSize > 200) pageSize = 200;

        var tenantId = user.GetTenantId();
        var drive = await driveRepo.GetByIdAsync(driveId, ct);
        if (drive is null) return Results.NotFound();

        IQueryable<FileItem> query = recursive
            ? repo.GetDescendantsQuery(driveId, path)
            : repo.GetChildrenQuery(driveId, path);

        var totalCount = await query.CountAsync(ct);
        var items = await query
            .OrderBy(f => f.IsDirectory ? 0 : 1).ThenBy(f => f.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(f => new FileItemDto(f))
            .ToListAsync(ct);

        return Results.Ok(new { items, page, pageSize, totalCount });
    }
}
```

## Acceptance Criteria

- [ ] `GET /api/v1/drives/{driveId}/files?path=/` → returns root-level items
- [ ] `recursive=true` → returns all nested items
- [ ] `pageSize` capped at 200 (no matter what client sends)
- [ ] Directories sorted before files (then alphabetically)
- [ ] Drive not accessible to user → `404 Not Found`
- [ ] Soft-deleted files excluded (global query filter)
- [ ] Requires `files.read` scope

## Test Cases

- **TC-001**: List root → immediate children only
- **TC-002**: `recursive=true` → all nested items
- **TC-003**: `page=2&pageSize=10` → correct pagination
- **TC-004**: `pageSize=999` → capped to 200
- **TC-005**: Deleted files absent from listing

## Implementation Tasks

- [ ] Create `FileEndpoints.cs` with `MapGet` registration
- [ ] Create `FileItemDto` record (maps `FileItem` to response shape)
- [ ] Register endpoint in `Program.cs`
- [ ] Add OpenAPI docs (`WithOpenApi()`, `WithSummary()`, `WithTags()`)

## Testing Tasks

- [ ] Integration test: list drive root → items returned
- [ ] Integration test: `pageSize=999` → 200 items max

## Security Review Checklist

- [ ] Endpoint requires auth
- [ ] Drive ownership verified (EF global filter handles tenant isolation)

## Code Review Checklist

- [ ] `pageSize` cap enforced server-side
- [ ] `FileItemDto` does not expose internal fields (`TenantId`, `StorageKey`)

## Definition of Done

- [ ] Endpoint returns paginated file list
- [ ] Integration test passes
