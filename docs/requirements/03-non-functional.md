# Non-Functional Requirements

## NFR-01: Performance

| Metric | Target |
|--------|--------|
| File upload throughput | ≥ 500 MB/s (local backend, single stream) |
| File download throughput | ≥ 1 GB/s (local backend, streaming) |
| Directory listing (1000 items) | < 100ms |
| GraphQL metadata query (p99) | < 200ms |
| TUS chunk acknowledgement | < 50ms (excluding I/O) |
| WebDAV PROPFIND (100 items) | < 150ms |

Performance is measured on the local filesystem backend with a warm database. Network-backed providers (GDrive, S3) are bound by the provider's throughput.

---

## NFR-02: Scalability

- v0.1: Single binary, single node, vertically scalable
- v0.2: Docker Compose; database on dedicated container
- v0.3: Kubernetes; stateless API pods scale horizontally; PostgreSQL via CloudNativePG with replicas
- The `IStorageProvider` abstraction ensures storage backends are replaceable without code changes
- All API pods are stateless (no sticky sessions); session state lives in JWT claims + database

---

## NFR-03: Reliability

- TUS protocol guarantees resumable uploads — no data loss on network interruption
- Outbox pattern guarantees event delivery (at-least-once) even if the process crashes mid-operation
- File versions are immutable blobs; overwrites never delete previous versions (when versioning is enabled)
- Database transactions wrap file record + outbox event — no partial state

---

## NFR-04: Security

| Requirement | Implementation |
|-------------|----------------|
| Audit log | All CRUD operations, logins, shares, and admin actions logged to `audit_entries` |
| Rate limiting | Per-user and per-IP limits; login brute-force lockout |
| Encryption at rest | AES-256 via `EncryptingStorageProvider` wrapper; pluggable `IKeyProvider` |
| TLS in transit | Required for all endpoints; WebDAV over HTTPS only |
| Auth | JWT Bearer tokens issued by embedded OpenIddict OIDC server |
| mTLS | Deferred to v0.3 (Kubernetes Linkerd service mesh) |
| Input validation | All API inputs validated; path traversal attacks blocked at `IStorageProvider` |
| Dependency auditing | `dotnet list package --vulnerable` in CI |

---

## NFR-05: Interoperability

- WebDAV RFC 4918 compliance for OS-native file system mounting
- OIDC standard (RFC 6749, 8414) for token issuance — any OAuth 2.0 client works
- TUS protocol for client-side upload libraries (JS, Python, Go, Swift, etc.)
- OpenAPI 3.1 spec published at `/openapi/v1.json`
- GraphQL introspection available for tooling (GraphiQL, Insomnia, etc.)

---

## NFR-06: Observability

| Signal | Tool |
|--------|------|
| Structured logs | Serilog → stdout (JSON) → collected by log aggregator |
| Metrics | OpenTelemetry → Prometheus endpoint `/metrics` |
| Tracing | OpenTelemetry → Jaeger or OTLP exporter |
| Health checks | `/health/ready` and `/health/live` (ASP.NET Core health checks) |
| Audit trail | `audit_entries` table queryable via admin GraphQL API |

---

## NFR-07: Developer Experience

- Single `dotnet run` starts the entire system (SQLite, embedded OIDC, local FS backend)
- `dotnet test` runs all unit and integration tests
- OpenAPI spec and GraphQL schema auto-generated from code — no manual spec files
- Plugin development: implement a C# interface, ship as NuGet package
- Migrations: EF Core migrations work on both SQLite and PostgreSQL

---

## NFR-08: Maintainability

- Clean Architecture: `Strg.Core` has zero infrastructure dependencies
- All external dependencies (DB, storage, auth) injected via interfaces
- Test coverage target: ≥ 80% on `Strg.Core` and `Strg.Api`
- Integration tests run against real SQLite (not mocked) to catch migration regressions
- Architectural Decision Records (ADRs) in `docs/decisions/`

---

## NFR-09: Licensing & Open Source

- License: Apache 2.0
- All dependencies must be compatible with Apache 2.0 (no GPL, AGPL)
- Plugin marketplace packages published under their own licenses
- CLA not required; DCO (Developer Certificate of Origin) sign-off per commit
