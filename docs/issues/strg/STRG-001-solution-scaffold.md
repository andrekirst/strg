---
id: STRG-001
title: Scaffold .NET 9 solution structure
milestone: v0.1
priority: critical
status: open
type: infrastructure
labels: [setup, core, dotnet]
depends_on: []
blocks: [STRG-002, STRG-003, STRG-004, STRG-006, STRG-007, STRG-008]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-001: Scaffold .NET 9 solution structure

## Summary

Create the complete .NET 9 solution file and all project files with correct inter-project references, package versions, and a consistent directory layout.

## Background / Context

All other implementation issues depend on this scaffold existing. The solution structure must enforce the clean architecture dependency rules from day one — `Strg.Core` must never reference infrastructure packages.

## Technical Specification

### Solution: `strg.sln`

### Projects to create

| Project                        | Type     | References                                                |
| ------------------------------ | -------- | --------------------------------------------------------- |
| `src/Strg.Core`                | classlib | none                                                      |
| `src/Strg.Infrastructure`      | classlib | Strg.Core                                                 |
| `src/Strg.GraphQL`             | classlib | Strg.Core                                                 |
| `src/Strg.WebDav`              | classlib | Strg.Core                                                 |
| `src/Strg.Api`                 | webapi   | Strg.Core, Strg.Infrastructure, Strg.GraphQL, Strg.WebDav |
| `tests/Strg.Core.Tests`        | xunit    | Strg.Core                                                 |
| `tests/Strg.Api.Tests`         | xunit    | Strg.Api, Strg.Infrastructure                             |
| `tests/Strg.Integration.Tests` | xunit    | Strg.Api                                                  |

### Directory Layout

```
src/
  Strg.Core/
    Domain/
    Storage/
    Identity/
    Plugins/
    Services/
  Strg.Infrastructure/
    Data/
    Storage/
    Identity/
    Events/
  Strg.GraphQL/
    Types/
    Queries/
    Mutations/
    Subscriptions/
  Strg.WebDav/
  Strg.Api/
    Endpoints/
tests/
  Strg.Core.Tests/
  Strg.Api.Tests/
  Strg.Integration.Tests/
```

### `Directory.Build.props` (repo root)

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <LangVersion>latest</LangVersion>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
  </PropertyGroup>
</Project>
```

### `.editorconfig` (repo root)

Standard C# rules: 4-space indent, `var` preference, braces required, file-scoped namespaces.

## Acceptance Criteria

- [ ] `strg.sln` exists at repo root and includes all 8 projects
- [ ] `dotnet build` succeeds with zero warnings and zero errors
- [ ] `dotnet test` runs (zero tests, but exits 0)
- [ ] `Strg.Core` has zero external NuGet package references
- [ ] `Strg.Infrastructure` does NOT reference `Strg.GraphQL` or `Strg.WebDav`
- [ ] All projects target `net9.0`
- [ ] `Nullable` and `ImplicitUsings` enabled globally via `Directory.Build.props`
- [ ] `TreatWarningsAsErrors` is `true`
- [ ] `.editorconfig` exists with C# formatting rules
- [ ] `Directory.Packages.props` (central package management) exists at repo root

## Test Cases

- **TC-001**: `dotnet build` → exit code 0, no warnings
- **TC-002**: `dotnet test` → exit code 0
- **TC-003**: Add a reference from `Strg.Core` to `Microsoft.EntityFrameworkCore` → build fails (architecture enforcement via `Directory.Build.props` project restriction, or documented convention)
- **TC-004**: `dotnet sln list` → shows all 8 projects

## Implementation Tasks

- [ ] Run `dotnet new sln -n strg`
- [ ] Create each project with `dotnet new classlib` / `dotnet new webapi` / `dotnet new xunit`
- [ ] Add all projects to solution: `dotnet sln add ...`
- [ ] Configure project-to-project references
- [ ] Create `Directory.Build.props` with global properties
- [ ] Create `Directory.Packages.props` for central package version management
- [ ] Create `.editorconfig`
- [ ] Create placeholder `README.md` files in each project explaining its responsibility
- [ ] Verify `dotnet build` and `dotnet test` pass

## Security Review Checklist

- [ ] No secrets or credentials in any project file
- [ ] `TreatWarningsAsErrors=true` is set (catches many security-relevant warnings)

## Code Review Checklist

- [ ] Dependency graph matches architecture spec in `docs/architecture/01-system-overview.md`
- [ ] All projects use `net9.0`
- [ ] `Directory.Build.props` applies globally

## Definition of Done

- [ ] All 8 projects created and buildable
- [ ] `dotnet build && dotnet test` passes from repo root
- [ ] Architecture dependency rules verified (core has no infra deps)
- [ ] CLAUDE.md updated with solution structure section
