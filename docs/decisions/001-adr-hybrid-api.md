# ADR-001: Hybrid REST + GraphQL API

**Date**: 2026-04-19
**Status**: Accepted

## Context

strg needs to expose an API that serves two very different workloads:

1. **File I/O** — large binary transfers (uploads up to hundreds of GB, streaming downloads). These are inherently HTTP streaming operations; they don't fit the request/response model of GraphQL.

2. **Metadata operations** — querying files by tags, listing drives, managing shares, subscribing to real-time events. These benefit from GraphQL's flexible query model, which eliminates over-fetching for different client types (CLI vs sync app vs future web UI).

## Decision

Use a **hybrid API**:

- **REST** for file I/O: TUS resumable uploads, streaming file downloads, WebDAV
- **GraphQL** (Hot Chocolate) for everything else: file metadata, tag queries, sharing, user management, admin, real-time subscriptions

## Rationale

### Why REST for file I/O?

- TUS is an HTTP-native protocol. It has no GraphQL equivalent.
- Streaming download via `Range` headers maps naturally to `GET` with HTTP 206.
- WebDAV is HTTP-native.
- File upload mutations in GraphQL require multipart extensions (`graphql-multipart-request-spec`) which are complex and not universally supported.

### Why GraphQL for metadata?

- Different clients need different fields. A CLI needs just `name` and `id`; a sync client needs `name`, `contentHash`, `updatedAt`, and `versionCount`. GraphQL eliminates the need for multiple REST endpoints with different response shapes.
- Tag filtering (`files(where: { tags: { some: { key: "project", value: "acme" } } })`) is expressive and avoids custom query parameter DSLs.
- GraphQL Subscriptions (WebSocket) for real-time events are cleaner than SSE and more powerful: clients can subscribe with filters.
- Hot Chocolate + EF Core's `IQueryable<T>` integration automatically translates GraphQL filter/sort/pagination into optimized SQL queries.

### Why Hot Chocolate?

- Best .NET GraphQL server library. Native EF Core IQueryable integration.
- Supports Relay spec (cursor pagination) out of the box.
- Schema-first and code-first modes; we use code-first for type safety.
- Active development and .NET 9 support.

## Consequences

- File operations (download/upload) remain at conventional REST paths `/api/v1/drives/{id}/files/{id}/content`
- All other API consumers must use GraphQL
- Plugin `IEndpointModule` can add both REST routes AND GraphQL type extensions
- OpenAPI spec covers the REST surface; GraphQL schema introspection covers the GraphQL surface
- Clients need to handle two different base URLs/protocols (REST and GraphQL), but this is standard for hybrid APIs

## Alternatives Rejected

| Alternative | Reason rejected |
|-------------|-----------------|
| Pure REST | Over-fetching, multiple endpoint shapes for different clients, complex filter query DSLs |
| Pure GraphQL | File upload/download is awkward; TUS doesn't work with GraphQL |
| gRPC | Poor browser support without gRPC-Web; binary protocol reduces debuggability |
| tRPC | TypeScript-first; doesn't align with C# backend |
