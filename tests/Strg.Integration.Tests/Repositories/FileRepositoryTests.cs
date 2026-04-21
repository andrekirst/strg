using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;
using Testcontainers.PostgreSql;
using Xunit;

namespace Strg.Integration.Tests.Repositories;

/// <summary>
/// STRG-033 TC-001..TC-005 against a real PostgreSQL instance. The global query filter behaviour
/// is the headline test target — a repository that accidentally ignores filters (via
/// <c>IgnoreQueryFilters()</c> or a raw SQL escape) silently leaks data across tenants, and that
/// kind of regression is hard to spot in a code review. The only way to prove the filter is
/// active is to write into one tenant context and read from another, so these tests explicitly
/// swap <see cref="ITenantContext"/> between write and read.
/// </summary>
public sealed class FileRepositoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact] // TC-001
    public async Task GetByIdAsync_returns_null_for_soft_deleted_file()
    {
        var tenantId = Guid.NewGuid();
        var options = await CreateFreshDatabaseAsync(tenantId);

        var driveId = await SeedDriveAsync(options, tenantId);
        var fileId = await SeedFileAsync(options, tenantId, driveId, "dead.txt");

        await using (var ctx = NewContext(options, tenantId))
        {
            var repo = new FileRepository(ctx);
            await repo.SoftDeleteAsync(fileId);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewContext(options, tenantId))
        {
            var repo = new FileRepository(ctx);
            var found = await repo.GetByIdAsync(fileId);
            found.Should().BeNull("the soft-delete filter hides DeletedAt-set rows");
        }
    }

    [Fact] // TC-002
    public async Task GetByIdAsync_returns_null_for_other_tenants_file()
    {
        var ownerTenant = Guid.NewGuid();
        var attackerTenant = Guid.NewGuid();
        var options = await CreateFreshDatabaseAsync(ownerTenant);

        // Seed tenant rows for both — required because Drive FK references Tenant.
        await SeedTenantAsync(options, attackerTenant, "attacker");
        var driveId = await SeedDriveAsync(options, ownerTenant);
        var fileId = await SeedFileAsync(options, ownerTenant, driveId, "secret.txt");

        await using var ctx = NewContext(options, attackerTenant);
        var repo = new FileRepository(ctx);
        var found = await repo.GetByIdAsync(fileId);

        found.Should().BeNull("the tenant filter scopes reads to the caller's tenant");
    }

    [Fact] // TC-003
    public async Task ListByParentAsync_returns_only_direct_children_not_grandchildren()
    {
        var tenantId = Guid.NewGuid();
        var options = await CreateFreshDatabaseAsync(tenantId);
        var driveId = await SeedDriveAsync(options, tenantId);

        var parentId = await SeedFolderAsync(options, tenantId, driveId, parentId: null, name: "parent", path: "parent");
        var childId = await SeedFileAsync(options, tenantId, driveId, "child.txt", parentId: parentId, path: "parent/child.txt");
        var subfolderId = await SeedFolderAsync(options, tenantId, driveId, parentId: parentId, name: "sub", path: "parent/sub");
        await SeedFileAsync(options, tenantId, driveId, "grandchild.txt", parentId: subfolderId, path: "parent/sub/grandchild.txt");

        await using var ctx = NewContext(options, tenantId);
        var repo = new FileRepository(ctx);
        var children = await repo.ListByParentAsync(driveId, parentId);

        children.Select(c => c.Id).Should().BeEquivalentTo([childId, subfolderId]);
        children.Should().NotContain(c => c.Name == "grandchild.txt");
    }

    [Fact] // TC-005
    public async Task ExistsByPath_returns_false_for_soft_deleted_file()
    {
        var tenantId = Guid.NewGuid();
        var options = await CreateFreshDatabaseAsync(tenantId);
        var driveId = await SeedDriveAsync(options, tenantId);
        var fileId = await SeedFileAsync(options, tenantId, driveId, "deleted.txt", path: "deleted.txt");

        await using (var ctx = NewContext(options, tenantId))
        {
            var repo = new FileRepository(ctx);
            await repo.SoftDeleteAsync(fileId);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewContext(options, tenantId))
        {
            var repo = new FileRepository(ctx);
            var found = await repo.GetByPathAsync(driveId, "deleted.txt");
            found.Should().BeNull("GetByPathAsync must honour soft-delete — otherwise restore-by-name would collide");
        }
    }

    [Fact]
    public async Task AddAsync_does_not_commit_until_caller_saves()
    {
        var tenantId = Guid.NewGuid();
        var options = await CreateFreshDatabaseAsync(tenantId);
        var driveId = await SeedDriveAsync(options, tenantId);

        // The contract under test: AddAsync stages the entity but the caller owns the
        // transaction boundary (CLAUDE.md repository pattern). A premature commit here would
        // break outbox-atomicity guarantees for services that stage multiple operations.
        await using (var ctx = NewContext(options, tenantId))
        {
            var repo = new FileRepository(ctx);
            var file = BuildFile(tenantId, driveId, name: "no-commit.txt", path: "no-commit.txt");
            await repo.AddAsync(file);
            // Intentionally no SaveChangesAsync here.
        }

        await using (var ctx = NewContext(options, tenantId))
        {
            var repo = new FileRepository(ctx);
            var found = await repo.GetByPathAsync(driveId, "no-commit.txt");
            found.Should().BeNull("repository must NOT auto-commit on Add");
        }
    }

    [Fact]
    public async Task ListByParentAsync_orders_folders_before_files()
    {
        var tenantId = Guid.NewGuid();
        var options = await CreateFreshDatabaseAsync(tenantId);
        var driveId = await SeedDriveAsync(options, tenantId);

        await SeedFileAsync(options, tenantId, driveId, "zeta.txt", path: "zeta.txt");
        await SeedFolderAsync(options, tenantId, driveId, parentId: null, name: "alpha", path: "alpha");
        await SeedFileAsync(options, tenantId, driveId, "alpha.txt", path: "alpha.txt");

        await using var ctx = NewContext(options, tenantId);
        var repo = new FileRepository(ctx);
        var rootItems = await repo.ListByParentAsync(driveId, parentId: null);

        // Folders first, then files alphabetically — the UI depends on this ordering.
        rootItems.Select(i => i.Name).Should().Equal(["alpha", "alpha.txt", "zeta.txt"]);
    }

    // --- helpers -------------------------------------------------------------------------------

    private async Task<DbContextOptions<StrgDbContext>> CreateFreshDatabaseAsync(Guid seedTenantId)
    {
        var dbName = $"strg_test_{Guid.NewGuid():N}";
        var adminConnection = _postgres.GetConnectionString();

        await using (var connection = new NpgsqlConnection(adminConnection))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{dbName}\"";
            await command.ExecuteNonQueryAsync();
        }

        var testConnection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = dbName }.ConnectionString;
        var options = new DbContextOptionsBuilder<StrgDbContext>()
            .UseNpgsql(testConnection)
            .Options;

        await using (var ctx = new StrgDbContext(options, new TestTenantContext(seedTenantId)))
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        // The seed tenant row is required because Drive.TenantId is a FK to Tenants.Id.
        await SeedTenantAsync(options, seedTenantId, "seed");
        return options;
    }

    private static async Task SeedTenantAsync(DbContextOptions<StrgDbContext> options, Guid tenantId, string name)
    {
        await using var ctx = NewContext(options, tenantId);
        // Fresh DB per test — this row never exists yet, so Add-then-Save is safe without a probe.
        ctx.Tenants.Add(new Tenant { Id = tenantId, Name = name });
        await ctx.SaveChangesAsync();
    }

    private static async Task<Guid> SeedDriveAsync(DbContextOptions<StrgDbContext> options, Guid tenantId)
    {
        var driveId = Guid.NewGuid();
        await using var ctx = NewContext(options, tenantId);
        ctx.Drives.Add(new Drive
        {
            Id = driveId,
            TenantId = tenantId,
            Name = $"drive-{driveId:N}",
            ProviderType = "memory",
            ProviderConfig = "{}",
        });
        await ctx.SaveChangesAsync();
        return driveId;
    }

    private static async Task<Guid> SeedFileAsync(
        DbContextOptions<StrgDbContext> options,
        Guid tenantId,
        Guid driveId,
        string name,
        Guid? parentId = null,
        string? path = null)
    {
        var file = BuildFile(tenantId, driveId, name, path ?? name, parentId, isDirectory: false);
        await using var ctx = NewContext(options, tenantId);
        ctx.Files.Add(file);
        await ctx.SaveChangesAsync();
        return file.Id;
    }

    private static async Task<Guid> SeedFolderAsync(
        DbContextOptions<StrgDbContext> options,
        Guid tenantId,
        Guid driveId,
        Guid? parentId,
        string name,
        string path)
    {
        var file = BuildFile(tenantId, driveId, name, path, parentId, isDirectory: true);
        await using var ctx = NewContext(options, tenantId);
        ctx.Files.Add(file);
        await ctx.SaveChangesAsync();
        return file.Id;
    }

    private static FileItem BuildFile(
        Guid tenantId,
        Guid driveId,
        string name,
        string path,
        Guid? parentId = null,
        bool isDirectory = false) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DriveId = driveId,
            ParentId = parentId,
            Name = name,
            Path = path,
            IsDirectory = isDirectory,
            Size = isDirectory ? 0 : 1,
            CreatedBy = Guid.NewGuid(),
        };

    private static StrgDbContext NewContext(DbContextOptions<StrgDbContext> options, Guid tenantId)
        => new(options, new TestTenantContext(tenantId));

    private sealed class TestTenantContext(Guid id) : ITenantContext
    {
        public Guid TenantId => id;
    }
}
