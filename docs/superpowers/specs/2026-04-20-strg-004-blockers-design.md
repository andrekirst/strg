---
title: STRG-004 remaining blockers — design
date: 2026-04-20
issue: STRG-004
scope: hard blockers only (Option A from brainstorm)
---

# STRG-004 Remaining Blockers — Design

## Context

STRG-004 established `StrgDbContext`, `ITenantContext`, entity configurations, and registration in `Program.cs`. Three hard blockers remain before the issue can be marked complete:

1. No initial EF Core migration exists. `src/Strg.Infrastructure/Migrations/` is absent.
2. The global query filter combines tenant isolation and soft-delete into a single expression — the issue spec explicitly requires two separate `HasQueryFilter` calls per entity.
3. Tests run against EF Core's `InMemory` provider rather than TestContainers PostgreSQL. The Phase 1 memory decision and the issue's testing tasks both require TestContainers.

Soft gaps (TC-005, `AclEntry`/`Share` DbSet reconciliation) are explicitly **out of scope**.

## Goals

- Produce a clean initial migration that EF Core tooling can generate and CI can apply.
- Refactor the global query filter to comply with the "two separate `HasQueryFilter` calls" rule.
- Replace EF InMemory tests with TestContainers-backed PostgreSQL tests, using the project's declared integration-tests layout.
- Add a durable convention that future work runs targeted tests, not the entire suite.

## Non-Goals

- Adding missing DbSets (`AclEntry`, `Share`) or the entity classes behind them.
- Adding the TC-005 (`IgnoreQueryFilters` bypass) test.
- Touching unrelated provider logic, Serilog configuration, or other tickets.

## Design

### 1. Initial EF Core Migration

`StrgDbContext`'s constructor requires `ITenantContext`. `dotnet ef` instantiates the context at design time with no DI container, so a design-time factory is required.

**New file:** `src/Strg.Infrastructure/Data/StrgDbContextFactory.cs`

```csharp
public sealed class StrgDbContextFactory : IDesignTimeDbContextFactory<StrgDbContext>
{
    public StrgDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<StrgDbContext>()
            .UseNpgsql("Host=localhost;Database=strg_design;Username=postgres;Password=postgres")
            .Options;
        return new StrgDbContext(options, new DesignTimeTenantContext());
    }

    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public Guid TenantId => Guid.Empty;
    }
}
```

The connection string is never dialed — `migrations add` only loads the model and emits SQL. The stub `ITenantContext` returns `Guid.Empty`; the global filter closure captures this as a constant during model caching, which is acceptable because the factory is never used at runtime.

**Command to run:**
```
dotnet ef migrations add Initial --project src/Strg.Infrastructure --startup-project src/Strg.Api
```

This produces `src/Strg.Infrastructure/Migrations/<timestamp>_Initial.cs` and `StrgDbContextModelSnapshot.cs`. Both are committed.

### 2. Split the global query filter

**File:** `src/Strg.Infrastructure/Data/StrgDbContext.cs`

Replace the single `BuildTenantFilter<T>()` that returns `e.TenantId == tenantContext.TenantId && !e.DeletedAt.HasValue` with two separate builders:

```csharp
private Expression<Func<T, bool>> BuildTenantFilter<T>() where T : TenantedEntity
    => e => e.TenantId == tenantContext.TenantId;

private Expression<Func<T, bool>> BuildSoftDeleteFilter<T>() where T : TenantedEntity
    => e => !e.DeletedAt.HasValue;
```

In `OnModelCreating`, invoke both via reflection and register each with its own `HasQueryFilter` call. EF Core 10 combines multiple filters with AND automatically.

The closure-over-`tenantContext` trick stays in place and the existing comment explaining why still applies — port it to the new `BuildTenantFilter<T>()`.

### 3. TestContainers integration tests

**Package additions to `Directory.Packages.props`:**
- `Testcontainers.PostgreSql` — pin to the latest stable 4.x at implementation time (verify via `dotnet nuget list` before committing).
- `Npgsql` — pinned explicitly; the test helper uses `NpgsqlConnection` / `NpgsqlConnectionStringBuilder` directly to create per-test databases. Version must match the major used by `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.1` (Npgsql 10.x).

**`tests/Strg.Integration.Tests/Strg.Integration.Tests.csproj` package references:**
- `Microsoft.EntityFrameworkCore`
- `Microsoft.EntityFrameworkCore.Design` (for tooling parity)
- `Npgsql.EntityFrameworkCore.PostgreSQL`
- `Testcontainers.PostgreSql`
- `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`
- `FluentAssertions`
- `coverlet.collector`

Project reference: `..\..\src\Strg.Infrastructure\Strg.Infrastructure.csproj`.

**File move:**
- `tests/Strg.Api.Tests/Data/StrgDbContextTests.cs` → `tests/Strg.Integration.Tests/Data/StrgDbContextTests.cs`
- Delete `tests/Strg.Api.Tests/Data/` after the move (only the one file lives there).

**Rewritten test class shape:**

```csharp
public sealed class StrgDbContextTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();
    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private async Task<DbContextOptions<StrgDbContext>> CreateFreshDatabaseAsync()
    {
        var dbName = $"strg_test_{Guid.NewGuid():N}";
        var adminConn = _postgres.GetConnectionString();

        await using (var conn = new NpgsqlConnection(adminConn))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
            await cmd.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(adminConn) { Database = dbName };
        var options = new DbContextOptionsBuilder<StrgDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .Options;

        await using (var ctx = new StrgDbContext(options, new SampleTenantContext(Guid.Empty)))
        {
            await ctx.Database.EnsureCreatedAsync();
        }
        return options;
    }

    // Each [Fact] calls CreateFreshDatabaseAsync() in its arrange step.
}
```

**Isolation semantics:**
- One container lives for the lifetime of the test class (Phase 12 convention).
- Each test method creates a unique PostgreSQL database inside that container.
- `EnsureCreatedAsync` builds the schema from model metadata — the migration itself is validated separately by the fact that `migrations add Initial` succeeds and CI will apply it.
- `StrgDbContext` is never instantiated with the test DB's own `ITenantContext` leaking into the shared model cache — EF Core keys the model by `DbContext` CLR type + options fingerprint, and each test uses fresh options.

**Tests kept (assertions unchanged; test-class infrastructure changes to `IAsyncLifetime` + per-test DB):**
- `Tenant_can_be_saved_and_loaded`
- `SaveChangesAsync_updates_UpdatedAt_on_modified_tenanted_entities`
- `Global_filter_excludes_entities_from_different_tenant`
- `Global_filter_excludes_soft_deleted_entities`

The `TestDbContext` helper (which adds `DbSet<SampleTenantedEntity>`) and `SampleTenantContext` move with the file.

**EF model cache caveat:** because `TestDbContext : StrgDbContext` and both are registered with different entity shapes, each must be instantiated via its own `DbContextOptions<StrgDbContext>`. This already works in the current tests; we're only swapping `UseInMemoryDatabase` for `UseNpgsql`.

### 4. CLAUDE.md update

Add under the existing `## Running Tests` section:

```markdown
### Only run affected tests while iterating

During implementation, prefer:

- `dotnet test --filter "FullyQualifiedName~<PartialName>"` to target specific test classes or methods.
- `dotnet test tests/<ProjectName>` to target a single test project.

Run the full suite (`dotnet test`) only once before declaring a task complete, committing, or opening a PR.
```

### 5. Feedback memory

Save as feedback memory so the convention sticks across sessions:

> Only run tests affected by the current change while iterating. Use `dotnet test --filter "FullyQualifiedName~…"` or a single-project target. Run the full suite only at the end, before committing or claiming completion.
> **Why:** User-stated collaboration preference, 2026-04-20.
> **How to apply:** any .NET test iteration in this or similar projects.

## Risks & open questions

- **TestContainers + Docker availability in CI.** Scope doesn't extend to CI config, so the assumption is that the CI runner already has Docker (standard for integration-tests projects). If not, follow-up issue.
- **`postgres:17-alpine` pin.** Chosen because it matches the documented prod target for v0.1 (Npgsql 10 supports PG 13+; 17 is the current LTS). If prod is pinned elsewhere, match that version in a follow-up.
- **`EnsureCreated` vs. applied migrations.** We're using `EnsureCreated` in tests for speed and simplicity. This means migrations are validated only by the `migrations add Initial` command succeeding, not by tests applying them. A later ticket can add a migration-smoke-test if needed.

## Definition of Done (for this spec)

- [ ] `src/Strg.Infrastructure/Data/StrgDbContextFactory.cs` exists.
- [ ] `src/Strg.Infrastructure/Migrations/*_Initial.cs` + `StrgDbContextModelSnapshot.cs` exist and build.
- [ ] `StrgDbContext.OnModelCreating` registers two separate `HasQueryFilter` calls per `TenantedEntity` subtype.
- [ ] `tests/Strg.Integration.Tests/Data/StrgDbContextTests.cs` exists with the four tests listed above, all green against a TestContainers PostgreSQL instance.
- [ ] `tests/Strg.Api.Tests/Data/` is removed.
- [ ] `Directory.Packages.props` includes `Testcontainers.PostgreSql`.
- [ ] `CLAUDE.md` has the "only affected tests" subsection.
- [ ] Feedback memory is saved.
- [ ] `dotnet test tests/Strg.Integration.Tests` passes locally (requires Docker).
- [ ] `dotnet build` is clean for the full solution.
