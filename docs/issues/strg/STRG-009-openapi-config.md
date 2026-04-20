---
id: STRG-009
title: Configure OpenAPI 3.1 spec generation via Swashbuckle
milestone: v0.1
priority: high
status: open
type: infrastructure
labels: [api, documentation, openapi]
depends_on: [STRG-001]
blocks: []
assigned_agent_type: general-purpose
estimated_complexity: small
---

# STRG-009: Configure OpenAPI 3.1 spec generation via Swashbuckle

## Summary

Configure Swashbuckle to auto-generate an OpenAPI 3.1 spec from code and expose it at `/openapi/v1.json`, `/openapi/v1.yaml`, and an interactive UI at `/openapi/ui` (dev only).

## Technical Specification

### Packages: `Swashbuckle.AspNetCore`

### Configuration:

```csharp
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "strg API",
        Version = "v1",
        Description = "Self-hosted cloud storage platform",
        License = new OpenApiLicense { Name = "Apache 2.0" }
    });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });
    c.AddSecurityRequirement(/* global Bearer requirement */);
    c.IncludeXmlComments(xmlPath);  // from project XML docs
});
```

### Endpoints:
- `GET /openapi/v1.json` — JSON spec
- `GET /openapi/v1.yaml` — YAML spec
- `GET /openapi/ui` — Swagger UI (dev only, behind `IsDevelopment()` check)

## Acceptance Criteria

- [ ] `/openapi/v1.json` returns valid OpenAPI 3.1 JSON
- [ ] Spec includes Bearer auth security definition
- [ ] Swagger UI accessible at `/openapi/ui` in development mode
- [ ] Swagger UI NOT accessible in production mode
- [ ] XML doc comments on endpoint methods appear in spec
- [ ] All REST endpoints (TUS, file download, etc.) appear in spec
- [ ] Response types are documented with correct status codes

## Test Cases

- **TC-001**: GET `/openapi/v1.json` → 200 with `application/json`
- **TC-002**: GET `/openapi/ui` in development → 200
- **TC-003**: GET `/openapi/ui` in production mode → 404
- **TC-004**: Add XML doc comment to endpoint → comment appears in spec

## Implementation Tasks

- [ ] Install Swashbuckle package
- [ ] Configure `AddSwaggerGen` with auth and info
- [ ] Enable XML documentation output in project file
- [ ] Register endpoints with environment-based conditions
- [ ] Add global `[Produces]` and `[ProducesResponseType]` attributes to base types

## Security Review Checklist

- [ ] Swagger UI disabled in production (cannot be used to explore prod API)
- [ ] Spec does not expose internal server paths or stack traces
- [ ] Bearer scheme correctly documented (not OAuth flow with client_secret in URL)

## Definition of Done

- [ ] JSON spec returns valid OpenAPI 3.1
- [ ] Swagger UI works in dev, blocked in prod
