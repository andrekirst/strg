# API Design

## Overview

strg exposes a **hybrid API**: REST for file I/O operations (upload, download, WebDAV), GraphQL for all metadata, queries, mutations, and real-time subscriptions.

```
REST  (file I/O)
  ├── TUS resumable uploads     PATCH  /upload/{uploadId}
  ├── File download             GET    /drives/{driveId}/files/{fileId}/content
  └── WebDAV                   *      /dav/{driveName}/

GraphQL (metadata + events)
  ├── Queries                  POST   /graphql
  ├── Mutations                POST   /graphql
  └── Subscriptions            WS     /graphql
```

---

## REST API

### Versioning

All REST endpoints are prefixed with `/api/v1/`. Breaking changes increment the major version. Non-breaking additions do not increment the version.

### Authentication

All endpoints require a JWT Bearer token issued by the embedded OpenIddict OIDC server:

```
Authorization: Bearer <jwt>
```

WebDAV: HTTP Basic auth exchanges username/password for a JWT internally (no plaintext passwords stored beyond the initial exchange).

Public share links: authenticated via share token in query string `?token=<shareToken>`.

### OpenAPI Spec

Auto-generated from code via Swashbuckle. Published at `/openapi/v1.json` and `/openapi/v1.yaml`. Interactive UI at `/openapi/ui` (development mode only).

### TUS Upload Endpoints

```
POST   /upload                  Initiate a new upload
HEAD   /upload/{uploadId}       Get upload offset (resume checkpoint)
PATCH  /upload/{uploadId}       Upload a chunk
DELETE /upload/{uploadId}       Abort an upload
```

TUS extension support: `creation`, `resumable`, `concatenation`, `checksum`.

### File Download

```
GET /drives/{driveId}/files/{fileId}/content
```

- Supports `Range` header for partial content (streaming media)
- Returns `Content-Disposition: attachment; filename="..."`
- ETag-based caching

---

## GraphQL API

### Schema Structure

```graphql
type Query {
  me: User
  drives: [Drive!]!
  drive(id: ID!): Drive
  files(driveId: ID!, where: FileFilterInput, order: [FileSortInput!], first: Int, after: String): FileConnection!
  file(id: ID!): File
  search(query: String!, driveId: ID, fullText: Boolean): [SearchResult!]!
  sharedWithMe: [SharedFile!]!
  auditLog(where: AuditFilterInput, first: Int, after: String): AuditConnection!
}

type Mutation {
  createDrive(input: CreateDriveInput!): Drive!
  deleteDrive(id: ID!): Boolean!
  createFolder(driveId: ID!, path: String!): File!
  deleteFile(id: ID!): Boolean!
  moveFile(id: ID!, targetPath: String!, targetDriveId: ID): File!
  copyFile(id: ID!, targetPath: String!, targetDriveId: ID): File!
  addTag(fileId: ID!, key: String!, value: String!): Tag!
  removeTag(fileId: ID!, key: String!): Boolean!
  createShare(input: CreateShareInput!): Share!
  revokeShare(id: ID!): Boolean!
  restoreVersion(fileId: ID!, versionId: ID!): File!
  createZipDownload(fileIds: [ID!]!): ZipDownloadJob!
  extractZip(fileId: ID!, targetDriveId: ID!, targetPath: String!): File!
}

type Subscription {
  fileEvents(driveId: ID): FileEvent!
  backupEvents: BackupEvent!
  quotaWarning: QuotaWarning!
}
```

### Pagination

All list queries follow the **Relay Cursor Pagination** spec:
- `first` / `after` for forward pagination
- `Connection { edges { node cursor } pageInfo { hasNextPage endCursor } }`

Hot Chocolate handles cursor generation automatically from EF Core `IQueryable<T>`.

### Filtering

```graphql
files(where: {
  tags: { some: { key: { eq: "project" }, value: { eq: "acme" } } }
  name: { contains: "invoice" }
  size: { gt: 1048576 }
  updatedAt: { gte: "2024-01-01" }
})
```

### Error Handling

GraphQL errors use the standard `errors` array with `extensions.code` for machine-readable error codes:

```json
{
  "errors": [{
    "message": "Quota exceeded",
    "extensions": { "code": "QUOTA_EXCEEDED", "used": 10737418240, "quota": 10737418240 }
  }]
}
```

REST errors use RFC 9457 Problem Details (`application/problem+json`).

---

## Plugin API Extensions

Plugins that implement `IEndpointModule` can register additional REST routes and contribute to the OpenAPI spec:

```csharp
public interface IEndpointModule
{
    void MapEndpoints(IEndpointRouteBuilder routes, IServiceCollection services);
    void ConfigureOpenApi(SwaggerGenOptions options);
}
```

GraphQL plugins can extend the schema by contributing additional query/mutation/subscription fields via Hot Chocolate's schema stitching or type extension mechanism.

---

## Rate Limiting

| Endpoint Category | Limit |
|-------------------|-------|
| Auth (token endpoint) | 10 req/min per IP |
| File upload (TUS) | 1000 chunks/min per user |
| GraphQL queries | 300 req/min per user |
| File download | 100 req/min per user |
| Admin endpoints | 60 req/min per user |

Returns `429 Too Many Requests` with `Retry-After` header.

Distributed rate limiting: Redis store for multi-pod deployments (v0.3). In-memory store for single-binary (v0.1).
