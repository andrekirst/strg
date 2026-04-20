# Deployment

## Evolution Path

```
v0.1: Single binary
v0.2: Docker + Docker Compose
v0.3: Kubernetes + Helm chart
```

---

## v0.1: Single Binary

The simplest deployment. No external dependencies except a filesystem.

```bash
# Development (SQLite, local FS)
dotnet run --project src/Strg.Api

# Configuration via environment variables or appsettings.json
STRG_DATABASE__PROVIDER=sqlite
STRG_DATABASE__CONNECTIONSTRING=Data Source=strg.db
STRG_STORAGE__DEFAULTPATH=/var/strg/data
```

The binary includes:
- ASP.NET Core HTTP server (Kestrel)
- Embedded OpenIddict OIDC server
- SQLite database
- Local filesystem storage backend
- WebDAV server
- MassTransit Outbox (in-memory transport, SQLite store)

---

## v0.2: Docker + Docker Compose

```yaml
# docker-compose.yml
version: '3.9'
services:
  strg:
    image: ghcr.io/andrekirst/strg:latest
    ports:
      - "5000:5000"
    environment:
      STRG_DATABASE__PROVIDER: postgres
      STRG_DATABASE__CONNECTIONSTRING: Host=db;Database=strg;Username=strg;Password=${DB_PASSWORD}
      STRG_STORAGE__DEFAULTPATH: /data
      STRG_SECURITY__ENCRYPTIONKEY: ${ENCRYPTION_KEY}
    volumes:
      - strg-data:/data
      - strg-plugins:/plugins
    depends_on:
      db:
        condition: service_healthy

  db:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: strg
      POSTGRES_USER: strg
      POSTGRES_PASSWORD: ${DB_PASSWORD}
    volumes:
      - pg-data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U strg"]
      interval: 5s
      timeout: 5s
      retries: 5

volumes:
  strg-data:
  strg-plugins:
  pg-data:
```

### Dockerfile

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Strg.Api -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data /plugins
EXPOSE 5000
ENTRYPOINT ["dotnet", "Strg.Api.dll"]
```

---

## v0.3: Kubernetes + Helm Chart

```yaml
# helm/values.yaml (excerpt)
replicaCount: 3

image:
  repository: ghcr.io/andrekirst/strg
  tag: "0.3.0"

database:
  type: postgresql
  host: strg-pg-primary  # CloudNativePG service name
  name: strg

cloudNativePG:
  enabled: true
  instances: 3  # 1 primary + 2 replicas
  storage:
    size: 50Gi

ingress:
  enabled: true
  className: nginx
  host: strg.example.com
  tls: true  # cert-manager Let's Encrypt

persistence:
  storageClass: fast-ssd
  size: 100Gi

linkerd:
  inject: true  # mTLS via Linkerd service mesh

autoscaling:
  enabled: true
  minReplicas: 2
  maxReplicas: 10
  targetCPUUtilizationPercentage: 70
```

### Kubernetes Resources

```
Deployment:     strg-api (3 replicas, stateless, HPA)
StatefulSet:    strg-db (via CloudNativePG operator)
PersistentVolumeClaim: strg-storage (file blobs)
PersistentVolumeClaim: strg-plugins (plugin DLLs)
ConfigMap:      strg-config
Secret:         strg-secrets (DB password, encryption key, OIDC signing cert)
Service:        strg-api (ClusterIP)
Ingress:        strg-ingress (nginx + cert-manager)
CronJob:        strg-backup (WAL-G + Restic, configurable schedule)
```

---

## Configuration Reference

All configuration is available via environment variables or `appsettings.json`. Environment variables take precedence.

| Key | Default | Description |
|-----|---------|-------------|
| `STRG_DATABASE__PROVIDER` | `sqlite` | `sqlite` or `postgres` |
| `STRG_DATABASE__CONNECTIONSTRING` | (SQLite file) | DB connection string |
| `STRG_STORAGE__DEFAULTPATH` | `./data` | Default local FS drive root |
| `STRG_SECURITY__ENCRYPTIONKEY` | (generated) | KEK for at-rest encryption |
| `STRG_SECURITY__JWTISSUER` | `https://localhost:5000` | OIDC issuer URI |
| `STRG_PLUGINS__PATH` | `./plugins` | Directory for plugin DLLs |
| `STRG_RATELIMITING__ENABLED` | `true` | Enable rate limiting |
| `STRG_OBSERVABILITY__OTLP_ENDPOINT` | (none) | OpenTelemetry collector URL |

---

## Reverse Proxy (Caddy example)

```caddyfile
strg.example.com {
    reverse_proxy localhost:5000
    encode gzip
    header {
        Strict-Transport-Security "max-age=31536000; includeSubDomains"
        X-Content-Type-Options "nosniff"
        X-Frame-Options "DENY"
    }
}
```
