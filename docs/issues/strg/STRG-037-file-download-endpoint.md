---
id: STRG-037
title: Implement file download streaming endpoint with Range support
milestone: v0.1
priority: critical
status: open
type: implementation
labels: [api, files, download]
depends_on: [STRG-031, STRG-024, STRG-013]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-037: Implement file download streaming endpoint with Range support

## Summary

Implement `GET /api/v1/drives/{driveId}/files/{fileId}/content` — streaming file download with HTTP Range request support (partial content) for seeking and media players.

## Technical Specification

### File: `src/Strg.Api/Endpoints/FileDownloadEndpoint.cs`

```csharp
app.MapGet("/api/v1/drives/{driveId}/files/{fileId}/content", async (
    Guid driveId, Guid fileId,
    HttpContext ctx,
    IFileService fileService,
    ClaimsPrincipal user) =>
{
    // 1. Verify user has read permission for this file
    // 2. Resolve IStorageProvider for the drive
    // 3. Parse Range header if present
    // 4. Stream file content with correct headers

    var file = await fileService.GetFileAsync(fileId, user.GetUserId());
    var provider = providerRegistry.Resolve(drive.ProviderType, drive.Config);

    var rangeHeader = ctx.Request.GetTypedHeaders().Range;
    if (rangeHeader is not null)
    {
        ctx.Response.StatusCode = 206;
        ctx.Response.Headers.ContentRange = $"bytes {start}-{end}/{file.Size}";
        await using var stream = await provider.ReadAsync(file.StoragePath, start, ctx.RequestAborted);
        await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }
    else
    {
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{file.Name}\"";
        ctx.Response.Headers.ContentType = file.MimeType;
        ctx.Response.Headers.ContentLength = file.Size;
        await using var stream = await provider.ReadAsync(file.StoragePath, 0, ctx.RequestAborted);
        await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }
});
```

## Acceptance Criteria

- [ ] `GET /api/v1/drives/{driveId}/files/{fileId}/content` returns file contents
- [ ] Response includes `Content-Disposition: attachment; filename="..."` header
- [ ] Response includes correct `Content-Type` header
- [ ] Response includes `Content-Length` header
- [ ] `Range: bytes=0-1023` header → `206 Partial Content` with correct bytes
- [ ] File larger than 1GB downloads without OOM (streaming, not buffered)
- [ ] Audit log entry created for every download
- [ ] Download of non-existent file → `404`
- [ ] Download without permission → `403`
- [ ] Download of directory → `400` (directories cannot be downloaded as content)

## Test Cases

- **TC-001**: GET file → 200 with correct content
- **TC-002**: GET file with `Range: bytes=0-99` → `206` with exactly 100 bytes
- **TC-003**: GET file with `Range: bytes=0-99` → `Content-Range: bytes 0-99/{total}` header
- **TC-004**: GET file user has no permission for → 403
- **TC-005**: GET file with invalid Range header → `416 Range Not Satisfiable`
- **TC-006**: Download 2GB file → memory usage stays under 50MB (streamed)
- **TC-007**: Client disconnects mid-download → no error logged (expected `OperationCanceledException`)

## Implementation Tasks

- [ ] Create `FileDownloadEndpoint.cs`
- [ ] Implement Range header parsing and response
- [ ] Implement streaming via `CopyToAsync` (never buffer to `MemoryStream`)
- [ ] Add audit log call
- [ ] Add ACL permission check (see STRG-075 for full ACL; for now, check drive ownership)
- [ ] Write integration tests including Range tests

## Security Review Checklist

- [ ] File path for storage lookup comes from DB (not user input)
- [ ] ACL checked before opening file stream
- [ ] `Content-Disposition` uses `attachment` (not `inline`) by default
- [ ] `Content-Type` comes from stored MIME type (not user-supplied)
- [ ] Cancel token respected (client disconnect stops I/O immediately)

## Definition of Done

- [ ] Streaming download works for multi-GB files
- [ ] Range requests work
- [ ] Audit log verified
