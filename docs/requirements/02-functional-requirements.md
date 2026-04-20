# Functional Requirements

## FR-01: Named Drives (Storage Volumes)

- Each drive is a named, independently configured storage volume
- Drives have a `provider_type` (e.g., `local`, `s3`, `gdrive`) and provider-specific configuration
- Users access drives they have permission to via `strg://{driveName}/`
- Drives are created and managed by administrators via API
- Each drive has an independent versioning retention policy

**Example drives:**
```
strg://home-nas/
strg://google-drive/
strg://backups/
strg://team-projects/
```

---

## FR-02: File Operations

| Operation | API | Protocol |
|-----------|-----|----------|
| Upload (any size) | TUS resumable | REST `PATCH /upload/{id}` |
| Download | Streaming | REST `GET /drives/{id}/files/{fileId}/content` |
| List directory | GraphQL | `query { files(driveId: ...) { ... } }` |
| Move / Copy | GraphQL mutation | `mutation { moveFile(from: ..., to: ...) }` |
| Delete | GraphQL mutation | `mutation { deleteFile(id: ...) }` |
| Create folder | GraphQL mutation | `mutation { createFolder(driveId: ..., name: ...) }` |
| Browse (Windows Explorer) | WebDAV RFC 4918 | `https://strg.host/dav/{driveName}/` |

---

## FR-03: File Versioning

- Versioning is configured per drive with a retention policy:
  - `none` — no versioning, overwrites replace the file
  - `count:N` — keep the last N versions
  - `days:N` — keep versions for the last N days
  - `unlimited` — keep all versions forever
- Users can list all versions of a file
- Users can download any historical version
- Users can restore a previous version (creates a new version)
- Version history survives file moves

---

## FR-04: Tags

- Files and folders can be tagged with key-value pairs
- Tags are **user-scoped**: each user maintains their own tag namespace
- Tag keys and values are arbitrary UTF-8 strings (max 255 chars each)
- No limit on number of tags per file per user
- Tags are queryable via GraphQL with filtering and sorting
- Auto-tagging via AI (future plugin, see FR-14)

**Schema:** `{ file_id, user_id, key, value }`

**Example queries:**
```graphql
query {
  files(where: { tags: { some: { key: "project", value: "acme" } } }) {
    name
    tags { key value }
  }
}
```

---

## FR-05: Access Control (ACL)

Every file and folder has an ACL. ACL entries specify:

| Field | Values |
|-------|--------|
| `principalType` | `user`, `group`, `publicLink` |
| `principalId` | userId, groupId, or share token |
| `permissions` | bitmask: `read`, `write`, `delete`, `share`, `manage` |
| `expiresAt` | Optional UTC timestamp |

**Inheritance**: folders inherit ACL entries from parent folders. Explicit entries override inherited ones.

**Public links**: generate a token-based share URL with optional password and download limit.

**Permission cache**: effective permissions are cached per (userId, resourceId) and invalidated on ACL change.

---

## FR-06: File Sharing

- Share a file or folder with internal users, groups, or via public link
- Public links support: optional password, expiry date, max download count
- Shared files appear in the recipient's "Shared with me" virtual folder
- Sharing an item with `share` permission allows the recipient to re-share
- All share actions are audit logged

---

## FR-07: Upload (TUS Protocol)

- All uploads use the [TUS](https://tus.io) resumable upload protocol
- Clients can resume interrupted uploads from the last acknowledged byte
- Quota is checked before accepting each chunk; upload fails with 413 if quota exceeded
- Parallel chunk uploads supported
- On completion: outbox event `file.uploaded` is written in the same DB transaction as the file record

---

## FR-08: WebDAV

- WebDAV server mounted at `/dav/{driveName}/`
- Implements RFC 4918 (PROPFIND, PROPPATCH, MKCOL, GET, PUT, DELETE, COPY, MOVE, LOCK, UNLOCK)
- Authentication: HTTP Basic (credentials exchanged for JWT internally)
- Compatible with: Windows Explorer, macOS Finder, GNOME Files, Nautilus, iOS Files app, Android file managers

---

## FR-09: Archive Management (ZIP)

| Feature | Description |
|---------|-------------|
| Browse ZIP as folder | A `.zip` stored in strg can be opened as a virtual directory |
| Server-side compression | Select files → server compresses → streams `archive.zip` to client |
| Server-side extraction | Extract a ZIP into a target folder, all on the server |
| No temp files | Streaming compression/decompression; no full archive buffered to disk |

ZIP files appear as virtual drives when accessed via the API. The `ZipStorageProvider` implements `IStorageProvider`.

---

## FR-10: Search

- **Default provider** (built-in): searches file names, paths, and tag key-value pairs
- **Plugin providers**: add full-text content search (Elasticsearch, MeiliSearch, Typesense, etc.)
- File type filters configure which MIME types are indexed for full-text (to limit resource usage)
- Search is scoped to the authenticated user's accessible files
- GraphQL query: `files(search: { query: "acme", fullText: true })`

---

## FR-11: Backup

The integrated backup engine protects:

| Scope | Mechanism |
|-------|-----------|
| File content | Restic-compatible deduplication to configurable backup destinations |
| PostgreSQL database | WAL-G continuous archiving + scheduled dumps |
| Configuration + secrets | Encrypted archive to backup destination |
| File version history | Included in file content backup (all version blobs are preserved) |

Backup destinations are configured as named targets (another drive, S3 bucket, SFTP server, etc.) and follow the `IStorageProvider` abstraction.

Schedules are configured via cron expressions. Restore is possible per file, per drive, or as a full point-in-time restore.

---

## FR-12: User Management & Quotas

- Administrators create and manage users via the admin GraphQL API
- Each user has a `quotaBytes` limit (hard limit, enforced on upload)
- Quota is configurable per user or per role
- Quota usage reported via GraphQL: `{ me { quotaBytes usedBytes freeBytes } }`
- Upload returns `413 Quota Exceeded` when the user's quota is full

---

## FR-13: Real-time Events

Events are delivered to clients via **GraphQL Subscriptions** (WebSocket transport):

| Event | Trigger |
|-------|---------|
| `file.uploaded` | File upload completed |
| `file.deleted` | File or folder deleted |
| `file.moved` | File or folder moved |
| `file.shared` | File shared with this user |
| `backup.completed` | Backup job finished |
| `quota.warning` | User exceeds 80% of quota |

Clients subscribe per drive or for their entire account. Events are sourced from the outbox.

---

## FR-14: Innovative Features (Future)

### AI Auto-tagging
- Plugin: `Strg.Plugin.AiTagger`
- Triggered on `file.uploaded` event
- Multimodal LLM (text + image analysis) suggests tags
- Configurable: auto-apply or require user confirmation

### AI File Assistant (Semantic Search)
- Plugin: `Strg.Plugin.SemanticSearch`
- Natural language queries: `"invoice from Acme November 2024"`
- RAG pipeline: file content → embeddings (pgvector) → LLM synthesis
- GraphQL: `semanticSearch(query: "...") { file { name } relevance snippet }`

### Federated Sharing (ActivityPub)
- Plugin: `Strg.Plugin.ActivityPub`
- strg instances can share drives with each other like Mastodon servers share posts
- Follow a remote drive: `@drive@remote.strg.instance`
- File events federated as ActivityPub `Activity` objects

### IPFS Backend
- Plugin: `Strg.Plugin.IpfsStorage` (implements `IStorageProvider`)
- Content-addressed storage: files stored by CID (content hash)
- Identical files across drives are automatically deduplicated
- Verifiable file integrity; decentralized backup
