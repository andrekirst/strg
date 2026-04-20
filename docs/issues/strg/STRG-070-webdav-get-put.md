---
id: STRG-070
title: Implement WebDAV GET and PUT handlers
milestone: v0.1
priority: high
status: open
type: implementation
labels: [webdav, upload, download]
depends_on: [STRG-068]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-070: Implement WebDAV GET and PUT handlers

## Summary

Implement WebDAV GET (file download) and PUT (file upload) handlers. PUT creates or replaces a file in the storage backend and updates the DB. GET streams file content to the client.

## Technical Specification

### GET handler (via NWebDav `StrgWebDavDocument`):

```csharp
public sealed class StrgWebDavDocument : IWebDavStoreDocument
{
    public async Task<Stream> GetReadableStreamAsync(
        IHttpContext httpContext,
        long startIndex,
        long count,
        CancellationToken ct)
    {
        var fullStream = await _provider.ReadAsync(_fileItem.StorageKey, ct);

        // Support range requests
        if (startIndex > 0)
        {
            fullStream.Seek(startIndex, SeekOrigin.Begin);
        }

        return fullStream;
    }

    public string ContentType => _fileItem.MimeType;
    public long ContentLength => _fileItem.Size;
    public string ETag => $"\"{_fileItem.ContentHash}\"";
}
```

### PUT handler:

```csharp
public sealed class StrgWebDavCollection : IWebDavStoreCollection
{
    public async Task<IWebDavStoreDocument> CreateDocumentAsync(
        string name,
        Stream content,
        IHttpContext httpContext,
        CancellationToken ct)
    {
        var path = StoragePath.Parse(_collectionPath).Combine(name);
        await _quotaService.CheckAsync(_userId, content.Length, ct);

        using var sha256 = SHA256.Create();
        var hashStream = new HashingStream(content, sha256);
        await _provider.WriteAsync(path.Value, hashStream, ct);
        var hash = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();

        var fileItem = new FileItem
        {
            DriveId = _drive.Id,
            TenantId = _drive.TenantId,
            Name = name,
            Path = path.Value,
            Size = hashStream.BytesWritten,
            ContentHash = $"sha256:{hash}",
            MimeType = httpContext.Request.ContentType ?? "application/octet-stream",
            CreatedBy = _userId
        };

        _db.Files.Add(fileItem);
        await _quotaService.CommitAsync(_userId, hashStream.BytesWritten, ct);
        await _db.SaveChangesAsync(ct);

        return new StrgWebDavDocument(fileItem, _provider);
    }
}
```

### HEAD support:

Return headers only (no body): `Content-Type`, `Content-Length`, `ETag`, `Last-Modified`.

### Range request support:

- `Range: bytes=0-1023` → stream bytes 0-1023 from storage provider
- `206 Partial Content` response

## Acceptance Criteria

- [ ] `GET /dav/{drive}/file.txt` → `200 OK` with file content stream
- [ ] `GET` with `Range: bytes=0-999` → `206 Partial Content` with correct bytes
- [ ] `PUT /dav/{drive}/new-file.txt` with body → file created in storage + DB
- [ ] PUT overwrites existing file → new `FileVersion` created, `FileItem` updated
- [ ] PUT quota check before write — exceeds quota → `507 Insufficient Storage`
- [ ] `HEAD /dav/{drive}/file.txt` → headers only, no body
- [ ] Content hash computed during PUT and stored on `FileItem`

## Test Cases

- **TC-001**: GET existing file → bytes match what was PUT
- **TC-002**: GET with `Range: bytes=100-199` → exactly 100 bytes returned
- **TC-003**: PUT 5MB file, quota is 4MB → `507`
- **TC-004**: PUT new file → `FileItem` in DB with correct `ContentHash`
- **TC-005**: HEAD → `Content-Length` header equals file size

## Implementation Tasks

- [ ] Implement `GetReadableStreamAsync` in `StrgWebDavDocument`
- [ ] Implement `CreateDocumentAsync` in `StrgWebDavCollection`
- [ ] Implement range request handling
- [ ] Implement HEAD response in `StrgWebDavDocument`
- [ ] Create `HashingStream` helper (wraps stream, computes hash on-the-fly)
- [ ] Map quota check failure → `507 Insufficient Storage`

## Testing Tasks

- [ ] Integration test: PUT then GET → bytes match
- [ ] Integration test: GET with Range header → 206
- [ ] Integration test: PUT exceeding quota → 507
- [ ] Unit test: `HashingStream` computes correct SHA-256 digest

## Security Review Checklist

- [ ] PUT validated by `[Authorize]` (files.write scope)
- [ ] `StoragePath.Parse()` called on file name (no path traversal via name)
- [ ] Content type from client not trusted for security decisions
- [ ] Quota checked BEFORE write (not after)

## Code Review Checklist

- [ ] `GetReadableStreamAsync` does NOT buffer entire file in memory
- [ ] `HashingStream` is disposed correctly (finalizes hash)
- [ ] `CreateDocumentAsync` rolls back quota on storage write failure

## Definition of Done

- [ ] Windows Explorer can open/save files via WebDAV
- [ ] Round-trip GET after PUT produces identical bytes
