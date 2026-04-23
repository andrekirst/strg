---
id: STRG-006
title: Configure Serilog structured logging
milestone: v0.1
priority: high
status: done
type: infrastructure
labels: [observability, logging]
depends_on: [STRG-001]
blocks: []
assigned_agent_type: general-purpose
estimated_complexity: small
---

# STRG-006: Configure Serilog structured logging

## Summary

Configure Serilog as the logging provider for structured JSON logging. Logs must be scoped with request context (userId, tenantId, traceId) and must never include secrets.

## Technical Specification

### Packages: `Serilog.AspNetCore`, `Serilog.Sinks.Console`, `Serilog.Enrichers.Environment`, `Serilog.Enrichers.Process`

### `appsettings.json`:
```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "System": "Warning",
        "Microsoft.EntityFrameworkCore": "Warning"
      }
    },
    "Enrich": ["FromLogContext", "WithMachineName", "WithEnvironmentName", "WithProcessId"],
    "WriteTo": [{ "Name": "Console", "Args": { "formatter": "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact" } }]
  }
}
```

### Middleware: Add `userId`, `tenantId`, and `appVersion` to log context on every request.

### Required enrichers on every log event:
- `RequestId` — ASP.NET Core correlation ID
- `UserId` — from JWT `sub` claim (if authenticated)
- `TenantId` — from JWT `tenant_id` claim (if available)
- `MachineName` — from `Serilog.Enrichers.Environment`
- `EnvironmentName` — from `Serilog.Enrichers.Environment` (Development/Production)
- `AppVersion` — read from `Assembly.GetExecutingAssembly().GetName().Version` on startup

## Acceptance Criteria

- [ ] Logs are structured JSON (not plain text) in production mode
- [ ] Every log line includes: timestamp, level, message, requestId, userId (if authenticated), tenantId (if available)
- [ ] Connection strings are never logged
- [ ] JWT tokens are never logged
- [ ] EF Core SQL queries are logged only at Debug level (not Information)
- [ ] `UseSerilogRequestLogging()` middleware is registered (logs each HTTP request as one line)

## Test Cases

- **TC-001**: Make an authenticated request → log line includes `UserId` field
- **TC-002**: Log a message containing a connection string → connection string not in output (destructuring policy)
- **TC-003**: Start app → first log line is structured JSON

## Implementation Tasks

- [ ] Install Serilog packages
- [ ] Configure Serilog in `Program.cs` via `UseSerilog()`
- [ ] Add log context enrichment middleware for userId/tenantId
- [ ] Add destructuring policy to scrub sensitive properties
- [ ] Test with development and production configurations

## Security Review Checklist

- [ ] Connection strings are scrubbed from logs
- [ ] JWT tokens (`Authorization` header) are never logged
- [ ] `Password` properties are excluded from destructured objects
- [ ] Stack traces from exceptions don't expose internal file paths in production

## Code Review Checklist

- [ ] `UseSerilogRequestLogging()` is called before routing middleware
- [ ] Log levels are appropriate (no debug spam in production defaults)

## Definition of Done

- [ ] Structured JSON logging works
- [ ] `userId` appears in authenticated request logs
- [ ] No secrets in log output verified manually
