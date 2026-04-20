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

- [ ] `StrgDbContext` compiles with all DbSets listed
- [ ] Only PostgreSQL provider configured (no SQLite in application code)
- [ ] Global query filters registered as **two separate** `HasQueryFilter` calls per entity (tenant isolation + soft-delete), not a combined expression
- [ ] Global query filter automatically scopes all `TenantedEntity` queries to current tenant
- [ ] Global query filter excludes soft-deleted records (`IsDeleted == false`) for all `TenantedEntity`
- [ ] `SaveChangesAsync` sets `UpdatedAt` on all modified `TenantedEntity` instances
- [ ] `ITenantContext` is defined in `Strg.Infrastructure` (or `Strg.Core`)
- [ ] Entity type configurations are in separate `IEntityTypeConfiguration<T>` classes
- [ ] `StrgDbContext` does NOT directly reference business logic services

## Test Cases

- **TC-001**: `dotnet ef migrations add Test --project Strg.Infrastructure` with Npgsql → succeeds
- **TC-002**: Save a `TenantedEntity` → `UpdatedAt` is updated automatically
- **TC-003**: Query `Users` with TenantId A configured → users from TenantId B not returned (tenant filter)
- **TC-004**: Query `Users` with `IsDeleted = true` → soft-deleted users not returned (soft-delete filter)
- **TC-005**: Query `Users` with `.IgnoreQueryFilters()` → all users returned (both filters bypassed)

## Implementation Tasks

- [ ] Create `src/Strg.Infrastructure/Data/StrgDbContext.cs`
- [ ] Create `src/Strg.Infrastructure/Data/ITenantContext.cs` and `HttpTenantContext.cs`
- [ ] Create `src/Strg.Infrastructure/Data/Configurations/` folder
- [ ] Write entity type configuration for `Tenant`
- [ ] Write global query filter helper for `TenantedEntity`
- [ ] Register `StrgDbContext` in `Strg.Api/Program.cs` with provider switch
- [ ] Install NuGet: `Npgsql.EntityFrameworkCore.PostgreSQL` only (no SQLite in application code)

## Testing Tasks

- [ ] Create `Strg.Api.Tests/Data/StrgDbContextTests.cs`
- [ ] Test soft-delete filter using TestContainers PostgreSQL
- [ ] Test tenant isolation filter
- [ ] Test `UpdatedAt` timestamp update on `SaveChangesAsync`
- [ ] Test that two separate filters (tenant + soft-delete) both apply and are AND-ed

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

- [ ] `StrgDbContext` created and registered
- [ ] Tests pass with SQLite in-memory
- [ ] `dotnet ef migrations add Initial` succeeds
