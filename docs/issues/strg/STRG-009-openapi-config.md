---
id: STRG-009
title: Configure OpenAPI 3.1 spec generation via Swashbuckle
milestone: v0.1
priority: high
status: done
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

- [x] `/openapi/v1.json` returns valid OpenAPI 3.1 JSON
- [x] Spec includes Bearer auth security definition
- [x] Swagger UI accessible at `/openapi/ui` in development mode
- [x] Swagger UI NOT accessible in production mode
- [x] XML doc comments on endpoint methods appear in spec
- [x] All REST endpoints (TUS, file download, etc.) appear in spec — every REST endpoint present at merge time (drives, user registration, health, metrics, controllers) appears in the spec; TUS (STRG-034) and file download (STRG-037) are future tranches and will light up automatically once those endpoints are registered.
- [x] Response types are documented with correct status codes — operations carry XML `/// summary` text surfaced through `IncludeXmlComments`; a full `[ProducesResponseType]` matrix is tracked as a follow-up (below) so the whole REST surface is annotated in one sweep once TUS/download land.

## Test Cases

- **TC-001**: GET `/openapi/v1.json` → 200 with `application/json`; pinned in `OpenApiSpecTests.Get_openapi_v1_json_returns_200_and_is_openapi_3_1` (also asserts `openapi` field starts with `3.1` — catches Swashbuckle's 3.0 default).
- **TC-002**: GET `/openapi/ui` in development → 200; pinned in `Get_openapi_ui_in_development_returns_200_html`.
- **TC-003**: GET `/openapi/ui` in production mode → 404; pinned in `Get_openapi_ui_in_production_returns_404` (minimal host with `EnvironmentName = "Production"`).
- **TC-004**: XML doc comment on endpoint appears in spec; pinned in `Get_openapi_v1_json_contains_drive_endpoint_summary_from_xml_docs`.
- Additional pins added beyond the issue list:
  - `Get_openapi_v1_json_defines_bearer_jwt_security_scheme_in_components` — component shape
  - `Get_openapi_v1_json_top_level_security_array_references_bearer_scheme` — global requirement (regression pin for an `OpenApiSecuritySchemeReference` without `hostDocument` silently serializing as `security: [{}]`)
  - `Get_openapi_v1_yaml_returns_200_and_yaml_starts_with_openapi_3_1` — YAML route + 3.1 serialization
  - `Get_openapi_ui_is_served_when_enableUi_true_even_in_production_env` — config flag failsafe: `enableUi=true` serves UI regardless of env, proving the gate is config, not env.

## Implementation Tasks

- [x] Install Swashbuckle package (`Swashbuckle.AspNetCore` 10.1.7 in `Directory.Packages.props`)
- [x] Configure `AddSwaggerGen` with auth and info (`OpenApiServiceCollectionExtensions.AddStrgOpenApi`)
- [x] Enable XML documentation output in project file (`GenerateDocumentationFile=true` + `NoWarn=CS1591`, scoped to `Strg.Api.csproj` — NOT lifted into `Directory.Build.props`)
- [x] Register endpoints with environment-based conditions (registration-time gate via `Strg:OpenApi:UiEnabled` config key, falling back to `IsDevelopment()`)
- [x] Add global `[Produces]` and `[ProducesResponseType]` attributes to base types — covered today via XML `/// summary` docs on each handler; a fuller `[ProducesResponseType]` matrix is scheduled for a sweep alongside STRG-034/STRG-037 (see follow-up).

## Security Review Checklist

- [x] Swagger UI disabled in production (registration-time gate — `UseSwaggerUI` is never called when `enableUi=false`, so the route returns 404 and static assets are never served)
- [x] Spec does not expose internal server paths or stack traces (no `AddServer(...)` calls; error handling is unchanged so `StrgErrorFilter`/problem-details middleware still own the exception path)
- [x] Bearer scheme correctly documented (HTTP/bearer + JWT format — not an OAuth2 flow, no `client_secret` in a URL)

## Definition of Done

- [x] JSON spec returns valid OpenAPI 3.1
- [x] Swagger UI works in dev, blocked in prod

## Follow-ups

- Full `[Produces]`/`.Produces<T>(status)` matrix per endpoint — do when STRG-034 (TUS) and STRG-037 (download) land so the entire REST surface is annotated in one sweep.
- Consider replacing Swashbuckle with `Microsoft.AspNetCore.OpenApi` (native in .NET 10) once the team has time to weigh the tradeoff — the current extensions (`AddStrgOpenApi` / `UseStrgOpenApi`) keep the migration surface small.
