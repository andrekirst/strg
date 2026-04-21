using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data;

public class StrgDbContext(DbContextOptions<StrgDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    public const string TenantFilterName = "Tenant";
    public const string SoftDeleteFilterName = "SoftDelete";

    private static readonly MethodInfo BuildTenantFilterMethod = typeof(StrgDbContext)
        .GetMethod(nameof(BuildTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo BuildSoftDeleteFilterMethod = typeof(StrgDbContext)
        .GetMethod(nameof(BuildSoftDeleteFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Drive> Drives => Set<Drive>();
    public DbSet<FileItem> Files => Set<FileItem>();
    public DbSet<FileVersion> FileVersions => Set<FileVersion>();
    public DbSet<FileKey> FileKeys => Set<FileKey>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<InboxRule> InboxRules => Set<InboxRule>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(StrgDbContext).Assembly);

        // Two named query filters per entity — EF Core 10+ ANDs them automatically at query time.
        // Unnamed filters would overwrite each other; named filters are also selectively disableable
        // via IgnoreQueryFilters(["TenantFilter"]).
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!entityType.ClrType.IsAssignableTo(typeof(TenantedEntity)))
            {
                continue;
            }

            var tenantFilter = (LambdaExpression)BuildTenantFilterMethod
                .MakeGenericMethod(entityType.ClrType)
                .Invoke(this, null)!;
            var softDeleteFilter = (LambdaExpression)BuildSoftDeleteFilterMethod
                .MakeGenericMethod(entityType.ClrType)
                .Invoke(this, null)!;

            modelBuilder.Entity(entityType.ClrType)
                .HasQueryFilter(TenantFilterName, tenantFilter)
                .HasQueryFilter(SoftDeleteFilterName, softDeleteFilter);
        }
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void UpdateTimestamps()
    {
        foreach (var entry in ChangeTracker.Entries<TenantedEntity>()
            .Where(e => e.State == EntityState.Modified))
        {
            entry.Entity.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    // Written as a generic C# lambda (not a hand-built expression tree) so EF Core recognises
    // `tenantContext.TenantId` as a closure reference to `this.tenantContext` and re-evaluates it
    // against the current DbContext instance at query time. A hand-built Expression.Constant(instance)
    // would freeze the first DbContext's tenantContext into the cached model.
    private Expression<Func<T, bool>> BuildTenantFilter<T>() where T : TenantedEntity
        => e => e.TenantId == tenantContext.TenantId;

    private Expression<Func<T, bool>> BuildSoftDeleteFilter<T>() where T : TenantedEntity
        => e => !e.DeletedAt.HasValue;
}
