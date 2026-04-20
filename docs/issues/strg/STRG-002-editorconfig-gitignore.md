---
id: STRG-002
title: Configure .editorconfig, .gitignore, and Directory.Packages.props
milestone: v0.1
priority: high
status: done
type: infrastructure
labels: [setup, tooling]
depends_on: [STRG-001]
blocks: []
assigned_agent_type: general-purpose
estimated_complexity: small
---

# STRG-002: Configure .editorconfig, .gitignore, and Directory.Packages.props

## Summary

Finalize repository-wide developer tooling: EditorConfig rules for consistent C# formatting, a comprehensive .gitignore, and central NuGet package version management via `Directory.Packages.props`.

## Technical Specification

### `.editorconfig`

Rules:
- `indent_style = space`, `indent_size = 4`
- `file_scoped_namespaces = true`
- `csharp_style_var_for_built_in_types = true:suggestion`
- `csharp_new_line_before_open_brace = all`
- `csharp_prefer_braces = true:error`
- Trim trailing whitespace, UTF-8, LF line endings

### `.gitignore`

Include: bin/, obj/, .vs/, .idea/, *.user, *.suo, TestResults/, .env, *.db, *.db-wal, *.db-shm, /data/, /plugins/, .superpowers/

### `Directory.Packages.props`

Central version pins for all NuGet packages used across the solution:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <!-- EF Core -->
    <PackageVersion Include="Microsoft.EntityFrameworkCore" Version="9.0.*" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" Version="9.0.*" />
    <PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="9.0.*" />
    <!-- Hot Chocolate GraphQL -->
    <PackageVersion Include="HotChocolate.AspNetCore" Version="14.*" />
    <PackageVersion Include="HotChocolate.Data.EntityFramework" Version="14.*" />
    <!-- OpenIddict -->
    <PackageVersion Include="OpenIddict.AspNetCore" Version="5.*" />
    <PackageVersion Include="OpenIddict.EntityFrameworkCore" Version="5.*" />
    <!-- MassTransit -->
    <PackageVersion Include="MassTransit" Version="8.*" />
    <PackageVersion Include="MassTransit.EntityFrameworkCore" Version="8.*" />
    <!-- TUS -->
    <PackageVersion Include="tusdotnet" Version="2.*" />
    <!-- WebDAV -->
    <PackageVersion Include="WebDav.Server" Version="*" />
    <!-- Logging / Observability -->
    <PackageVersion Include="Serilog.AspNetCore" Version="8.*" />
    <PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.*" />
    <!-- Validation -->
    <PackageVersion Include="FluentValidation.AspNetCore" Version="11.*" />
    <!-- Testing -->
    <PackageVersion Include="xunit" Version="2.*" />
    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="9.0.*" />
    <PackageVersion Include="FluentAssertions" Version="6.*" />
    <PackageVersion Include="NSubstitute" Version="5.*" />
  </ItemGroup>
</Project>
```

## Acceptance Criteria

- [x] `.editorconfig` enforces file-scoped namespaces
- [x] `.editorconfig` enforces braces-required (`csharp_prefer_braces = true:error`)
- [x] `.gitignore` excludes `*.db`, `.env`, `data/`, `plugins/`
- [x] `.gitignore` excludes `.superpowers/`
- [x] `Directory.Packages.props` pins all major dependencies
- [x] `ManagePackageVersionsCentrally = true` is set
- [x] Running `dotnet restore` after this issue succeeds

## Test Cases

- **TC-001**: Create a C# file without braces on an if statement → build emits error (csharp_prefer_braces:error)
- **TC-002**: Create a file with a non-file-scoped namespace → analyzer warns
- **TC-003**: Add a package to a project without a version in `Directory.Packages.props` → build error

## Implementation Tasks

- [x] Write `.editorconfig` with all C# rules
- [x] Write `.gitignore` (use GitHub's C# template as base, add strg-specific entries)
- [x] Write `Directory.Packages.props` with all packages from the tech stack
- [x] Verify `dotnet build` and `dotnet restore` pass after changes

## Security Review Checklist

- [x] `.gitignore` excludes `.env` files
- [x] `.gitignore` excludes `*.db` (SQLite files with potential user data)
- [x] `Directory.Packages.props` does not pin insecure/outdated versions

## Code Review Checklist

- [x] All packages from the tech stack are included in `Directory.Packages.props`
- [x] Version ranges are appropriate (not too loose, not too strict)

## Definition of Done

- [x] All three files committed
- [x] `dotnet build` passes with zero warnings
