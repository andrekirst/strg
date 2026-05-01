---
id: STRG-339
title: REST GET /files/{fileId}/thumbnail with ETag/304/202-pending
milestone: v0.2
priority: high
status: open
type: feature
labels: [thumbnails, phase-15, rest-api, caching]
depends_on: [STRG-329, STRG-331]
blocks: [STRG-340, STRG-343]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-339: REST GET /files/{fileId}/thumbnail with ETag/304/202-pending

## Summary

Streaming REST endpoint that serves a thumbnail blob for a given `(fileId, variant)`. Honours `If-None-Match` for 304 responses, returns `202 Accepted` while generation is still pending, and applies the project's standard auth policy (same read-permission gate as the file download endpoint).

## Background / Context

Thumbnails are content-addressed — same `FileVersion.ContentHash` always produces the same WebP. This is a natural fit for a strong ETag and `Cache-Control: immutable`. Browsers will cache aggressively, which is critical for grid-view performance (otherwise every gallery scroll triggers N HTTP fetches for the same WebP).

`202 Accepted` exists because thumbnails are async (D1) — the file may have just been uploaded and the consumer hasn't finished yet. Returning `404` would be misleading; `202` with `Retry-After` tells the client "come back in 5 s".

## Technical Specification

### Endpoint — `src/Strg.Api/Endpoints/ThumbnailEndpoints.cs`

```csharp
public static class ThumbnailEndpoints
{
    public static IEndpointRouteBuilder MapThumbnailEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/files/{fileId:guid}/thumbnail", GetThumbnailAsync)
           .RequireAuthorization()
           .WithName("GetFileThumbnail")
           .WithTags("Thumbnails");

        return app;
    }

    private static async Task<IResult> GetThumbnailAsync(
        Guid fileId,
        [FromQuery] string variant,
        ClaimsPrincipal user,
        IThumbnailRepository thumbnails,
        StrgDbContext db,
        IStorageProvider storageProvider,
        IFileAuthorizationService auth,                // existing service from STRG-037 download
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!ThumbnailVariants.All.Contains(variant))
        {
            return Results.BadRequest(new { error = "unknown-variant", variant });
        }

        var fileVersion = await db.FileVersions
            .AsNoTracking()
            .Where(v => v.FileId == fileId)
            .OrderByDescending(v => v.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (fileVersion is null) { return Results.NotFound(); }

        // Auth — same policy as the download endpoint.
        if (!await auth.CanReadAsync(user, fileId, cancellationToken))
        {
            return Results.Forbid();
        }

        var entry = await thumbnails.GetAsync(fileVersion.Id, variant, "webp", cancellationToken);
        if (entry is null)
        {
            // Generation not yet started — 202 with Retry-After.
            http.Response.Headers["Retry-After"] = "5";
            return Results.StatusCode(StatusCodes.Status202Accepted);
        }

        return entry.Status switch
        {
            ThumbnailStatus.Pending =>
                AcceptedWithRetryAfter(http),
            ThumbnailStatus.Unsupported or ThumbnailStatus.Failed =>
                Results.NotFound(),
            ThumbnailStatus.Ready =>
                await StreamAsync(entry, fileVersion, storageProvider, http, cancellationToken),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }
}
```

### `StreamAsync` — ETag, 304, streaming response

```csharp
private static async Task<IResult> StreamAsync(
    ThumbnailEntry entry, FileVersion fileVersion,
    IStorageProvider storageProvider, HttpContext http,
    CancellationToken cancellationToken)
{
    var etag = $"\"{fileVersion.ContentHash}-{entry.Variant}-{entry.Format}\"";

    // Strong ETag, content-addressed — If-None-Match is exact match.
    if (http.Request.Headers.IfNoneMatch.Contains(etag))
    {
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    var path = StoragePath.Parse(entry.StorageKey);
    var stream = await storageProvider.ReadAsync(path.Value, 0, cancellationToken);
    var contentType = entry.Format == "jpeg"
        ? MediaTypeNames.Image.Jpeg
        : "image/webp";    // not yet in MediaTypeNames as of net10

    http.Response.Headers[HeaderNames.ETag] = etag;
    http.Response.Headers[HeaderNames.CacheControl] = "private, max-age=31536000, immutable";
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";

    return Results.File(stream, contentType: contentType, enableRangeProcessing: false);
}
```

### Status code matrix

| Condition | Status | Headers |
|---|---|---|
| `variant` not in whitelist | 400 | `{"error":"unknown-variant"}` |
| File missing | 404 | — |
| User lacks read permission | 403 | — |
| Row absent (consumer not yet hit) | 202 | `Retry-After: 5` |
| Row `Pending` | 202 | `Retry-After: 5` |
| Row `Failed` or `Unsupported` | 404 | — (do NOT leak the reason — the file exists, just no thumbnail) |
| Row `Ready` + `If-None-Match` match | 304 | `ETag` echoed |
| Row `Ready` + new request | 200 | `Content-Type`, `ETag`, `Cache-Control: immutable`, `X-Content-Type-Options: nosniff` |

### Why no Range processing

WebPs at our variant sizes are tiny (typically <500 KiB). Range support adds 304/206 complexity without benefit. Set `enableRangeProcessing: false`.

### Why `private` not `public` cache

Thumbnails are tenant-scoped — public CDN caching would leak cross-tenant if the same `fileId` GUID happened across tenants (it shouldn't, but defense in depth). `private` says "browser cache OK, shared cache NO".

## Acceptance Criteria

- [ ] `GET /files/{fileId}/thumbnail?variant=...` is mapped and requires auth.
- [ ] Variant whitelist enforced (400 on unknown).
- [ ] Auth via `IFileAuthorizationService.CanReadAsync` (same policy as download — STRG-037).
- [ ] Status codes: 200/202/304/400/403/404 per matrix.
- [ ] ETag is strong, composite of `ContentHash + Variant + Format`.
- [ ] `Cache-Control: private, max-age=31536000, immutable`.
- [ ] `X-Content-Type-Options: nosniff` set on 200 responses.
- [ ] Streaming via `IStorageProvider.ReadAsync` — no `byte[]` buffer.
- [ ] All thumbnail keys flow through `StoragePath.Parse()` before storage I/O.

## Test Cases

- **TC-001**: GET ready thumbnail → 200, `Content-Type: image/webp`, `ETag` present, `Cache-Control: immutable`, body is a valid WebP.
- **TC-002**: GET with matching `If-None-Match` → 304, no body, `ETag` echoed.
- **TC-003**: GET while row is `Pending` → 202, `Retry-After: 5`.
- **TC-004**: GET when row absent (consumer hasn't fired yet) → 202, `Retry-After: 5`.
- **TC-005**: GET when row is `Unsupported` (encrypted-drive carve-out) → 404.
- **TC-006**: GET with `variant=enormous` → 400 with `{"error":"unknown-variant"}`.
- **TC-007**: GET as user without read permission → 403.
- **TC-008**: Manual `curl -I` shows `Cache-Control: private, max-age=31536000, immutable` and `ETag`.

## Implementation Tasks

- [ ] Add `ThumbnailEndpoints` and call `app.MapThumbnailEndpoints()` in `Program.cs`.
- [ ] Reuse the existing `IFileAuthorizationService` from STRG-037 — do NOT duplicate auth logic.
- [ ] Set the cache headers via `HeaderNames.*` constants (no magic strings).
- [ ] Tests under `tests/Strg.Integration.Tests/Thumbnails/ThumbnailEndpointTests.cs`.

## Security Review Checklist

- [ ] Auth check happens BEFORE any blob I/O — no info leak about file existence to unauthorized callers (404 on auth fail vs 403 — chose 403 because the user MIGHT see the file in a listing they have list-permission on; aligns with download endpoint).
- [ ] `If-None-Match` is exact-match (strong ETag), not weak/W/ comparison.
- [ ] `Cache-Control: private` (not `public`) — no shared-cache cross-tenant leak.
- [ ] `X-Content-Type-Options: nosniff` blocks browser MIME-sniffing on the WebP.
- [ ] No `entry.ErrorReason` leaked in 404 response (the API does not reveal generation failure modes to unauthenticated query patterns).
- [ ] All paths through `StoragePath.Parse()`.
- [ ] No `entry.StorageKey` reflected back in any response header or body.

## Code Review Checklist

- [ ] `MediaTypeNames.Image.Jpeg` constant for JPEG; `"image/webp"` literal acceptable (not yet in BCL constants).
- [ ] `HeaderNames.ETag`, `HeaderNames.CacheControl` from `Microsoft.Net.Http.Headers` (no magic strings).
- [ ] `Results.File` with `enableRangeProcessing: false`.
- [ ] No ContextAccessor abuse — `HttpContext` is a method parameter.
- [ ] CancellationToken parameter named `cancellationToken`.

## Definition of Done

- [ ] All acceptance criteria green.
- [ ] Integration tests TC-001…TC-007 named and passing.
- [ ] Manual `curl -I` smoke captured in the PR description.
