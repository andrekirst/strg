using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;
using Testcontainers.PostgreSql;
using Xunit;

namespace Strg.Integration.Tests.Data;

internal sealed class SampleTenantedEntity : TenantedEntity { }

internal sealed class SampleTenantContext(Guid id) : ITenantContext
{
    public Guid TenantId => id;
}

internal sealed class TestDbContext(DbContextOptions<StrgDbContext> options, ITenantContext tc)
    : StrgDbContext(options, tc)
{
    public DbSet<SampleTenantedEntity> Samples => Set<SampleTenantedEntity>();
}

public sealed class StrgDbContextTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private async Task<DbContextOptions<StrgDbContext>> CreateFreshDatabaseAsync<TContext>(ITenantContext tenantContext)
        where TContext : StrgDbContext
    {
        var dbName = $"strg_test_{Guid.NewGuid():N}";
        var adminConnectionString = _postgres.GetConnectionString();

        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{dbName}\"";
            await command.ExecuteNonQueryAsync();
        }

        var testDbConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = dbName,
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<StrgDbContext>()
            .UseNpgsql(testDbConnectionString)
            .Options;

        await using (var ctx = (TContext)Activator.CreateInstance(typeof(TContext), options, tenantContext)!)
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        return options;
    }

    [Fact]
    public async Task Tenant_can_be_saved_and_loaded()
    {
        var tenantId = Guid.NewGuid();
        var options = await CreateFreshDatabaseAsync<StrgDbContext>(new SampleTenantContext(tenantId));
        var tenant = new Tenant { Name = "Acme Corp" };

        await using (var ctx = new StrgDbContext(options, new SampleTenantContext(tenantId)))
        {
            ctx.Tenants.Add(tenant);
            await ctx.SaveChangesAsync();
        }

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
        var tenantId = Guid.NewGuid();
        var options = await CreateFreshDatabaseAsync<TestDbContext>(new SampleTenantContext(tenantId));
        var entity = new SampleTenantedEntity { TenantId = tenantId };
        DateTimeOffset originalUpdatedAt;

        await using (var ctx = new TestDbContext(options, new SampleTenantContext(tenantId)))
        {
            ctx.Samples.Add(entity);
            await ctx.SaveChangesAsync();
            originalUpdatedAt = entity.UpdatedAt;
        }

        await Task.Delay(10);

        DateTimeOffset updatedAt;
        await using (var ctx = new TestDbContext(options, new SampleTenantContext(tenantId)))
        {
            var loaded = await ctx.Samples.IgnoreQueryFilters().FirstAsync(e => e.Id == entity.Id);
            ctx.Entry(loaded).State = EntityState.Modified;
            await ctx.SaveChangesAsync();
            updatedAt = loaded.UpdatedAt;
        }

        updatedAt.Should().BeAfter(originalUpdatedAt);
    }

    [Fact]
    public async Task Global_filter_excludes_entities_from_different_tenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var options = await CreateFreshDatabaseAsync<TestDbContext>(new SampleTenantContext(tenantA));
        var entityA = new SampleTenantedEntity { TenantId = tenantA };
        var entityB = new SampleTenantedEntity { TenantId = tenantB };

        await using (var ctx = new TestDbContext(options, new SampleTenantContext(tenantA)))
        {
            ctx.Samples.Add(entityA);
            ctx.Samples.Add(entityB);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = new TestDbContext(options, new SampleTenantContext(tenantA)))
        {
            var results = await ctx.Samples.ToListAsync();
            results.Should().ContainSingle();
            results[0].Id.Should().Be(entityA.Id);
        }
    }

    [Fact]
    public async Task Global_filter_excludes_soft_deleted_entities()
    {
        var tenantId = Guid.NewGuid();
        var options = await CreateFreshDatabaseAsync<TestDbContext>(new SampleTenantContext(tenantId));
        var active = new SampleTenantedEntity { TenantId = tenantId };
        var deleted = new SampleTenantedEntity { TenantId = tenantId };

        await using (var ctx = new TestDbContext(options, new SampleTenantContext(tenantId)))
        {
            ctx.Samples.Add(active);
            ctx.Samples.Add(deleted);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = new TestDbContext(options, new SampleTenantContext(tenantId)))
        {
            var toDelete = await ctx.Samples.FirstAsync(e => e.Id == deleted.Id);
            toDelete.DeletedAt = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = new TestDbContext(options, new SampleTenantContext(tenantId)))
        {
            var results = await ctx.Samples.ToListAsync();
            results.Should().ContainSingle();
            results[0].Id.Should().Be(active.Id);
        }
    }
}
