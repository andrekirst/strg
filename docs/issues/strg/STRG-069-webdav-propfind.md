---
id: STRG-069
title: Implement WebDAV PROPFIND handler
milestone: v0.1
priority: high
status: open
type: implementation
labels: [webdav]
depends_on: [STRG-068]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-069: Implement WebDAV PROPFIND handler

## Summary

Implement the PROPFIND request handler that returns file and directory property XML responses. Supports both `Depth: 0` (item properties) and `Depth: 1` (directory listing) as required by RFC 4918.

## Technical Specification

### Properties to expose per RFC 4918 §15:

| DAV Property | Mapped From |
|---|---|
| `{DAV:}displayname` | `FileItem.Name` |
| `{DAV:}getcontentlength` | `FileItem.Size` |
| `{DAV:}getcontenttype` | `FileItem.MimeType` |
| `{DAV:}getetag` | `FileItem.ContentHash` (quoted) |
| `{DAV:}getlastmodified` | `FileItem.UpdatedAt` (RFC 1123 format) |
| `{DAV:}creationdate` | `FileItem.CreatedAt` (ISO 8601) |
| `{DAV:}resourcetype` | `<collection/>` for dirs, empty for files |

### Response format (multi-status 207):

```xml
<?xml version="1.0" encoding="utf-8"?>
<D:multistatus xmlns:D="DAV:">
  <D:response>
    <D:href>/dav/my-drive/docs/</D:href>
    <D:propstat>
      <D:prop>
        <D:displayname>docs</D:displayname>
        <D:resourcetype><D:collection/></D:resourcetype>
        <D:getlastmodified>Mon, 01 Jan 2024 00:00:00 GMT</D:getlastmodified>
      </D:prop>
      <D:status>HTTP/1.1 200 OK</D:status>
    </D:propstat>
  </D:response>
</D:multistatus>
```

### Handler wired via NWebDav:

NWebDav handles PROPFIND routing automatically via `IWebDavStore`. The `StrgWebDavStore` and `StrgWebDavCollection`/`StrgWebDavDocument` types from STRG-068 implement the NWebDav interfaces that supply property values.

No separate handler class is needed — the NWebDav dispatcher calls the store's `GetItemAsync` and reads properties from the returned `IWebDavStoreItem`.

### Custom strg properties (namespace `strg:`):

```xml
<strg:contenthash>sha256:abc123...</strg:contenthash>
<strg:version>3</strg:version>
```

These appear in PROPFIND responses as custom dead properties.

## Acceptance Criteria

- [ ] `PROPFIND /dav/{drive}/ Depth: 0` → 207 with properties of root collection
- [ ] `PROPFIND /dav/{drive}/ Depth: 1` → 207 with properties of root AND all immediate children
- [ ] `PROPFIND /dav/{drive}/file.txt Depth: 0` → 207 with file properties
- [ ] `resourcetype` is `<collection/>` for directories, empty for files
- [ ] `getetag` is `ContentHash` in quoted format (`"sha256:abc"`)
- [ ] `getlastmodified` in RFC 1123 format
- [ ] `Depth: infinity` → supported, but capped at configurable item limit (default 10,000); returns `507 Insufficient Storage` if cap exceeded
- [ ] Custom `strg:contenthash` and `strg:version` in response

### `Depth: infinity` cap configuration:

```json
{
  "WebDav": {
    "PropfindInfinityMaxItems": 10000
  }
}
```

If the recursive listing would return more than `PropfindInfinityMaxItems` items, the handler returns `507 Insufficient Storage` rather than completing the response.

## Test Cases

- **TC-001**: `PROPFIND / Depth: 1` → child items listed with correct hrefs
- **TC-002**: File item → `resourcetype` empty, dir → `<collection/>`
- **TC-003**: `Depth: infinity` on a drive with 5 items → all 5 returned (under cap)
- **TC-004**: `getetag` value matches `FileItem.ContentHash`
- **TC-005**: `strg:contenthash` present in PROPFIND response
- **TC-006**: `Depth: infinity` on a drive with items exceeding cap → `507 Insufficient Storage`

## Implementation Tasks

- [ ] Implement property mapping in `StrgWebDavDocument` and `StrgWebDavCollection`
- [ ] Add custom strg namespace properties
- [ ] Implement `Depth: infinity` with configurable item cap (`WebDav:PropfindInfinityMaxItems`, default 10,000)
- [ ] Return `507 Insufficient Storage` when cap exceeded
- [ ] Bind cap from `IConfiguration` in NWebDav middleware setup
- [ ] Verify RFC 1123 date formatting for `getlastmodified`

## Testing Tasks

- [ ] Integration test: PROPFIND response is valid XML
- [ ] Integration test: child items appear with correct `href` paths
- [ ] Integration test: `Depth: infinity` under cap → all items returned
- [ ] Integration test: `Depth: infinity` over cap → `507`

## Security Review Checklist

- [ ] `Depth: infinity` capped to prevent recursive listing DoS (default 10,000)
- [ ] `href` values do not expose physical storage paths
- [ ] Properties do not leak `TenantId` or `DriveId` (internal IDs)

## Code Review Checklist

- [ ] RFC 1123 date format uses `R` format specifier
- [ ] ETag is double-quoted per HTTP spec

## Definition of Done

- [ ] Windows Explorer can browse directory listing via WebDAV
- [ ] PROPFIND response passes RFC 4918 compliance check
