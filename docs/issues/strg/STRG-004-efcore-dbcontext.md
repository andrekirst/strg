---
id: STRG-004
title: Create EF Core DbContext with PostgreSQL and global query filters
milestone: v0.1
priority: critical
status: open
type: implementation
labels: [infrastructure, database, efcore]
depends_on: [STRG-003]
blocks: [STRG-005, STRG-012, STRG-022, STRG-032]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-004: Create EF Core DbContext with multi-provider support and global query filters

## Summary

Create `StrgDbContext` in `Strg.Infrastructure` with support for both SQLite (development) and PostgreSQL (production), global query filters for soft-delete and tenant isolation, and the `UpdatedAt` auto-update interceptor.

## Background / Context

`StrgDbContext` is the heart of the data access layer. It uses **PostgreSQL exclusively** (via Npgsql). Global query filters ensure every query is automatically scoped to the current tenant and excludes soft-deleted records. Two separate `HasQueryFilter` calls are registered per entity type — one for tenant isolation, one for soft-delete — EF Core ANDs them automatically.

## Technical Specification

### File: `src/Strg.Infrastructure/Data/StrgDbContext.cs`

```csharp
public class StrgDbContext(DbContextOptions<StrgDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Drive> Drives => Set<Drive>();
    public DbSet<FileItem> Files => Set<FileItem>();
    public DbSet<FileVersion> FileVersions => Set<FileVersion>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<AclEntry> AclEntries => Set<AclEntry>();
    public DbSet<Share> Shares => Set<Share>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(StrgDbContext).Assembly);

        // Global query filters: tenant isolation + soft delete
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.ClrType.IsAssignableTo(typeof(TenantedEntity)))
            {
                modelBuilder.Entity(entityType.ClrType)
                    .HasQueryFilter(BuildTenantFilter(entityType.ClrType));
            }
        }
    }

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        UpdateTimestamps();
        return base.SaveChangesAsync(ct);
    }

    private void UpdateTimestamps()
    {
        foreach (var entry in ChangeTracker.Entries<TenantedEntity>()
            .Where(e => e.State == EntityState.Modified))
        {
            entry.Entity.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }
}
```

### File: `src/Strg.Infrastructure/Data/ITenantContext.cs`

```csharp
public interface ITenantContext
{
    Guid TenantId { get; }
}
```

### Registration (in `Strg.Api/Program.cs`):

```csharp
builder.Services.AddDbContext<StrgDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("Connection string 'Default' not configured.");
    options.UseNpgsql(connectionString);
});
```

### Global query filter pattern (two separate filters per entity):

```csharp
// In OnModelCreating — register both filters separately (EF Core ANDs them)
modelBuilder.Entity<FileItem>()
    .HasQueryFilter(f => f.TenantId == tenantContext.TenantId);  // tenant isolation
modelBuilder.Entity<FileItem>()
    .HasQueryFilter(f => !f.IsDeleted);  // soft-delete exclusion
```

## Acceptance Criteria

- [ ] `StrgDbContext` compiles with all DbSets listed _(DbSets present: Tenants, Users, Drives, Files, FileVersions, Tags, AuditEntries, InboxRules. `AclEntries` / `Shares` from original spec are deferred — entities not yet defined; `Share` is scoped to STRG-111.)_
- [x] Only PostgreSQL provider configured (no SQLite in application code)
- [x] Global query filters registered as **two separate** `HasQueryFilter` calls per entity (tenant isolation + soft-delete), not a combined expression _(implemented as EF Core 10 named filters — `TenantFilterName` and `SoftDeleteFilterName` — which are ANDed automatically)_
- [x] Global query filter automatically scopes all `TenantedEntity` queries to current tenant
- [x] Global query filter excludes soft-deleted records (`IsDeleted == false`) for all `TenantedEntity`
- [x] `SaveChangesAsync` sets `UpdatedAt` on all modified `TenantedEntity` instances
- [x] `ITenantContext` is defined in `Strg.Infrastructure` (or `Strg.Core`)
- [x] Entity type configurations are in separate `IEntityTypeConfiguration<T>` classes
- [x] `StrgDbContext` does NOT directly reference business logic services

## Test Cases

- [x] **TC-001**: `dotnet ef migrations add Initial --project Strg.Infrastructure` with Npgsql → succeeds
- [x] **TC-002**: Save a `TenantedEntity` → `UpdatedAt` is updated automatically
- [x] **TC-003**: Query with TenantId A configured → entities from TenantId B not returned (tenant filter)
- [x] **TC-004**: Query with `DeletedAt` set → soft-deleted entities not returned (soft-delete filter)
- [ ] **TC-005**: Query with `.IgnoreQueryFilters()` → all entities returned (both filters bypassed) _(deferred — out of scope for current blocker sweep)_

## Implementation Tasks

- [x] Create `src/Strg.Infrastructure/Data/StrgDbContext.cs`
- [x] Create `src/Strg.Infrastructure/Data/ITenantContext.cs` and `HttpTenantContext.cs`
- [x] Create `src/Strg.Infrastructure/Data/Configurations/` folder
- [x] Write entity type configuration for `Tenant`
- [x] Write global query filter helper for `TenantedEntity`
- [x] Register `StrgDbContext` in `Strg.Api/Program.cs` (PostgreSQL only per Phase 1 decision)
- [x] Install NuGet: `Npgsql.EntityFrameworkCore.PostgreSQL` only (no SQLite in application code)
- [x] Add `StrgDbContextFactory : IDesignTimeDbContextFactory<StrgDbContext>` so `dotnet ef` can instantiate the context at design time
- [x] Generate initial migration (`src/Strg.Infrastructure/Migrations/*_Initial.cs` + `StrgDbContextModelSnapshot.cs`)

## Testing Tasks

- [x] Create `tests/Strg.Integration.Tests/Data/StrgDbContextTests.cs` (moved from `Strg.Api.Tests/Data/` per integration-test layout)
- [x] Test soft-delete filter using TestContainers PostgreSQL
- [x] Test tenant isolation filter
- [x] Test `UpdatedAt` timestamp update on `SaveChangesAsync`
- [x] Test that two separate filters (tenant + soft-delete) both apply and are AND-ed _(covered implicitly by the tenant and soft-delete behavioral tests each passing when both filters are registered)_

## Security Review Checklist

- [ ] Connection string is never logged (check Serilog configuration)
- [ ] No raw SQL queries in DbContext (use LINQ/EF Core APIs only)
- [ ] `TenantId` global filter cannot be trivially bypassed by callers
- [ ] `IgnoreQueryFilters()` usage must be explicitly justified (admin-only scenarios)

## Code Review Checklist

- [ ] Entity configurations use `IEntityTypeConfiguration<T>` (not OnModelCreating bloat)
- [ ] `SaveChangesAsync` override calls `base.SaveChangesAsync`
- [ ] Provider switch logic is clean and doesn't require code changes for new providers

## Definition of Done

- [x] `StrgDbContext` created and registered
- [x] Tests pass against TestContainers PostgreSQL (Phase 1 decision superseded the original SQLite-in-memory wording)
- [x] `dotnet ef migrations add Initial` succeeds

## Remaining spec gaps (deferred)

- `AclEntries` / `Shares` DbSets listed in the original spec are not implemented — entities don't exist yet (`Share` is scoped to STRG-111; `AclEntry` is not currently tracked).
- TC-005 (`IgnoreQueryFilters()` bypass) test not added.

Both are non-blocking for downstream work (STRG-005, STRG-012, STRG-022, STRG-032) and can be picked up in a follow-up.
