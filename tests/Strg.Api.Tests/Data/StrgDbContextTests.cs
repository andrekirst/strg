using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;
using Xunit;

namespace Strg.Api.Tests.Data;

// Test helper entity — internal to test assembly, not exposed outside
internal sealed class SampleTenantedEntity : TenantedEntity { }

internal sealed class SampleTenantContext(Guid id) : ITenantContext
{
    public Guid TenantId => id;
}

// Test-only DbContext that registers the test entity so it can be stored
internal sealed class TestDbContext : StrgDbContext
{
    public TestDbContext(DbContextOptions<StrgDbContext> options, ITenantContext tc)
        : base(options, tc) { }

    public DbSet<SampleTenantedEntity> Samples => Set<SampleTenantedEntity>();
}

public sealed class StrgDbContextTests
{
    // Each call creates a fresh isolated InMemory database root to prevent cross-test contamination
    private static DbContextOptions<StrgDbContext> CreateOptions()
        => new DbContextOptionsBuilder<StrgDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString(), new InMemoryDatabaseRoot())
            .Options;

    [Fact]
    public async Task Tenant_can_be_saved_and_loaded()
    {
        // Arrange
        var options = CreateOptions();
        var tenantId = Guid.NewGuid();
        var tenant = new Tenant { Name = "Acme Corp" };

        // Act
        await using (var ctx = new StrgDbContext(options, new SampleTenantContext(tenantId)))
        {
            ctx.Tenants.Add(tenant);
            await ctx.SaveChangesAsync();
        }

        // Assert — Tenant does NOT inherit TenantedEntity, so no global filter applies
        await using (var ctx = new StrgDbContext(options, new SampleTenantContext(tenantId)))
        {
            var loaded = await ctx.Tenants.FindAsync(tenant.Id);
            loaded.Should().NotBeNull();
            loaded!.Name.Should().Be("Acme Corp");
        }
    }

    [Fact]
    public async Task SaveChangesAsync_updates_UpdatedAt_on_modified_tenanted_entities()
    {
        // Arrange
        var options = CreateOptions();
        var tenantId = Guid.NewGuid();
        var entity = new SampleTenantedEntity { TenantId = tenantId };
        DateTimeOffset originalUpdatedAt;

        await using (var ctx = new TestDbContext(options, new SampleTenantContext(tenantId)))
        {
            ctx.Samples.Add(entity);
            await ctx.SaveChangesAsync();
            originalUpdatedAt = entity.UpdatedAt;
        }

        // Act — small delay to ensure time advances
        await Task.Delay(10);

        DateTimeOffset updatedAt;
        await using (var ctx = new TestDbContext(options, new SampleTenantContext(tenantId)))
        {
            var loaded = await ctx.Samples.IgnoreQueryFilters().FirstAsync(e => e.Id == entity.Id);
            ctx.Entry(loaded).State = EntityState.Modified;
            await ctx.SaveChangesAsync();
            updatedAt = loaded.UpdatedAt;
        }

        // Assert
        updatedAt.Should().BeAfter(originalUpdatedAt);
    }

    [Fact]
    public async Task Global_filter_excludes_entities_from_different_tenant()
    {
        // Arrange
        var options = CreateOptions();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var entityA = new SampleTenantedEntity { TenantId = tenantA };
        var entityB = new SampleTenantedEntity { TenantId = tenantB };

        // Seed both entities (saving as tenant A — filter is only on queries, not inserts)
        await using (var ctx = new TestDbContext(options, new SampleTenantContext(tenantA)))
        {
            ctx.Samples.Add(entityA);
            ctx.Samples.Add(entityB);
            await ctx.SaveChangesAsync();
        }

        // Act — query as tenant A
        await using (var ctx = new TestDbContext(options, new SampleTenantContext(tenantA)))
        {
            var results = await ctx.Samples.ToListAsync();

            // Assert — only tenant A's entity is visible
            results.Should().ContainSingle();
            results[0].Id.Should().Be(entityA.Id);
        }
    }

    [Fact]
    public async Task Global_filter_excludes_soft_deleted_entities()
    {
        // Arrange
        var options = CreateOptions();
        var tenantId = Guid.NewGuid();
        var active = new SampleTenantedEntity { TenantId = tenantId };
        var deleted = new SampleTenantedEntity { TenantId = tenantId };

        await using (var ctx = new TestDbContext(options, new SampleTenantContext(tenantId)))
        {
            ctx.Samples.Add(active);
            ctx.Samples.Add(deleted);
            await ctx.SaveChangesAsync();
        }

        // Soft-delete one entity (set DeletedAt — IsDeleted is computed from this)
        await using (var ctx = new TestDbContext(options, new SampleTenantContext(tenantId)))
        {
            var toDelete = await ctx.Samples.FirstAsync(e => e.Id == deleted.Id);
            toDelete.DeletedAt = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync();
        }

        // Act
        await using (var ctx = new TestDbContext(options, new SampleTenantContext(tenantId)))
        {
            var results = await ctx.Samples.ToListAsync();

            // Assert — only the non-deleted entity is returned
            results.Should().ContainSingle();
            results[0].Id.Should().Be(active.Id);
        }
    }
}
