# Data Model

## Design Principles

- **TenantId on every table**: Single-org now, multi-tenant upgrade path preserved
- **Soft deletes**: `deleted_at` timestamp; hard delete only on explicit purge
- **Immutable version blobs**: File versions are never overwritten
- **Outbox in same schema**: Events written atomically with the data change

---

## Entity Relationship Overview

```
tenants
  └── users (quota, roles)
  └── drives (provider config, versioning policy)
       └── file_items (tree structure via parent_id)
            └── file_versions (immutable blobs)
            └── tags (user-scoped key-value)
            └── acl_entries (permission grants)
  └── shares (public link tokens)
  └── audit_entries (append-only)
  └── outbox_events (MassTransit outbox)
```

---

## Tables

### `tenants`
```sql
id          UUID PK
name        VARCHAR(255) NOT NULL
created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
```

### `users`
```sql
id           UUID PK
tenant_id    UUID FK→tenants NOT NULL
email        VARCHAR(320) NOT NULL UNIQUE (tenant-scoped)
display_name VARCHAR(255) NOT NULL
quota_bytes  BIGINT NOT NULL DEFAULT 10737418240  -- 10 GB default
created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
updated_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
deleted_at   TIMESTAMPTZ NULL
```

### `drives`
```sql
id                    UUID PK
tenant_id             UUID FK→tenants NOT NULL
name                  VARCHAR(255) NOT NULL
provider_type         VARCHAR(64) NOT NULL   -- 'local', 's3', 'gdrive', 'ipfs', ...
provider_config       JSONB NOT NULL DEFAULT '{}'
versioning_policy     JSONB NOT NULL DEFAULT '{"mode":"none"}'
encryption_enabled    BOOLEAN NOT NULL DEFAULT false
created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW()
updated_at            TIMESTAMPTZ NOT NULL DEFAULT NOW()
deleted_at            TIMESTAMPTZ NULL
```

`versioning_policy` examples:
```json
{ "mode": "none" }
{ "mode": "count", "keep": 30 }
{ "mode": "days", "keepDays": 90 }
{ "mode": "unlimited" }
```

### `file_items`
```sql
id              UUID PK
tenant_id       UUID FK→tenants NOT NULL
drive_id        UUID FK→drives NOT NULL
parent_id       UUID FK→file_items NULL  -- NULL for root items
name            VARCHAR(1024) NOT NULL
path            TEXT NOT NULL             -- materialized path for fast tree queries
size            BIGINT NOT NULL DEFAULT 0
content_hash    VARCHAR(64) NULL          -- SHA-256 of current content
is_directory    BOOLEAN NOT NULL DEFAULT false
created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
created_by      UUID FK→users NOT NULL
deleted_at      TIMESTAMPTZ NULL

INDEX (tenant_id, drive_id, parent_id)
INDEX (tenant_id, drive_id, path)
```

### `file_versions`
```sql
id              UUID PK
file_id         UUID FK→file_items NOT NULL
version_number  INT NOT NULL
size            BIGINT NOT NULL
content_hash    VARCHAR(64) NOT NULL
storage_key     TEXT NOT NULL   -- opaque path/key within the storage backend
created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
created_by      UUID FK→users NOT NULL

UNIQUE (file_id, version_number)
```

### `tags`
```sql
id          UUID PK
tenant_id   UUID FK→tenants NOT NULL
file_id     UUID FK→file_items NOT NULL
user_id     UUID FK→users NOT NULL
key         VARCHAR(255) NOT NULL
value       VARCHAR(255) NOT NULL

UNIQUE (file_id, user_id, key)
INDEX (tenant_id, user_id, key, value)
```

### `acl_entries`
```sql
id                UUID PK
tenant_id         UUID FK→tenants NOT NULL
resource_id       UUID NOT NULL          -- file_item id or drive id
resource_type     VARCHAR(32) NOT NULL   -- 'file', 'folder', 'drive'
principal_type    VARCHAR(32) NOT NULL   -- 'user', 'group', 'public'
principal_id      UUID NULL              -- null for public links
permissions       INT NOT NULL           -- bitmask: read=1, write=2, delete=4, share=8, manage=16
expires_at        TIMESTAMPTZ NULL
created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
created_by        UUID FK→users NOT NULL

INDEX (tenant_id, resource_id, resource_type)
INDEX (tenant_id, principal_type, principal_id)
```

### `shares`
```sql
id              UUID PK
tenant_id       UUID FK→tenants NOT NULL
resource_id     UUID NOT NULL
resource_type   VARCHAR(32) NOT NULL
token           VARCHAR(64) NOT NULL UNIQUE
permissions     INT NOT NULL
password_hash   VARCHAR(256) NULL
max_downloads   INT NULL
download_count  INT NOT NULL DEFAULT 0
expires_at      TIMESTAMPTZ NULL
created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
created_by      UUID FK→users NOT NULL
revoked_at      TIMESTAMPTZ NULL
```

### `audit_entries`
```sql
id              UUID PK (append-only, no updates, no deletes)
tenant_id       UUID NOT NULL
user_id         UUID NULL    -- null for anonymous/system actions
action          VARCHAR(64) NOT NULL  -- 'file.upload', 'file.delete', 'share.create', ...
resource_id     UUID NOT NULL
resource_type   VARCHAR(32) NOT NULL
ip_address      INET NULL
metadata        JSONB NOT NULL DEFAULT '{}'
timestamp       TIMESTAMPTZ NOT NULL DEFAULT NOW()

INDEX (tenant_id, timestamp DESC)
INDEX (tenant_id, user_id, timestamp DESC)
INDEX (tenant_id, resource_id, timestamp DESC)
```

### `outbox_events`
```sql
id              UUID PK
tenant_id       UUID NOT NULL
type            VARCHAR(128) NOT NULL   -- 'file.uploaded', 'file.deleted', ...
payload         JSONB NOT NULL
created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
processed_at    TIMESTAMPTZ NULL
error           TEXT NULL
attempt_count   INT NOT NULL DEFAULT 0

INDEX (processed_at NULLS FIRST, created_at)
```

### OpenIddict Tables
OpenIddict manages its own EF Core tables (`openiddict_applications`, `openiddict_tokens`, `openiddict_authorizations`, `openiddict_scopes`). These are tenant-agnostic in v0.1.

---

## Permissions Bitmask

| Permission | Bit value | Description |
|------------|-----------|-------------|
| Read | 1 | List and download files |
| Write | 2 | Upload and modify files |
| Delete | 4 | Delete files and folders |
| Share | 8 | Create shares and ACL entries |
| Manage | 16 | Change drive config, ACL, versioning |

Bitwise OR combines permissions: `Read + Write = 3`, `Read + Write + Delete = 7`.

---

## Multi-tenancy

All tables except OpenIddict carry `tenant_id`. EF Core **global query filters** automatically scope all queries to the current request's tenant:

```csharp
modelBuilder.Entity<FileItem>().HasQueryFilter(f => f.TenantId == _tenantContext.TenantId);
```

In v0.1 there is one tenant. Multi-tenant mode is enabled by routing on hostname or path prefix in v0.3.
