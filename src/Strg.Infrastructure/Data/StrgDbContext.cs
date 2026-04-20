using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data;

public class StrgDbContext(DbContextOptions<StrgDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    // Additional DbSets will be added in STRG-011, STRG-025, STRG-031, STRG-046

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(StrgDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.ClrType.IsAssignableTo(typeof(TenantedEntity)))
            {
                modelBuilder.Entity(entityType.ClrType)
                    .HasQueryFilter(BuildCombinedFilter(entityType.ClrType));
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

    private LambdaExpression BuildCombinedFilter(Type entityClrType)
    {
        var param = Expression.Parameter(entityClrType, "e");

        // TenantId == tenantContext.TenantId  (evaluated at query time)
        var tenantIdProp = Expression.Property(param, nameof(TenantedEntity.TenantId));
        var tenantContextConst = Expression.Constant(tenantContext);
        var tenantIdValue = Expression.Property(tenantContextConst, nameof(ITenantContext.TenantId));
        var tenantFilter = Expression.Equal(tenantIdProp, tenantIdValue);

        // !DeletedAt.HasValue  (IsDeleted is a computed property, use DeletedAt instead)
        var deletedAtProp = Expression.Property(param, nameof(TenantedEntity.DeletedAt));
        var hasValueProp = Expression.Property(deletedAtProp, nameof(Nullable<DateTimeOffset>.HasValue));
        var notDeleted = Expression.Not(hasValueProp);

        var combined = Expression.AndAlso(tenantFilter, notDeleted);
        return Expression.Lambda(combined, param);
    }
}
