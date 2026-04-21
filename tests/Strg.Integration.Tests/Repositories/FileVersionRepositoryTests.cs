using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;
using Testcontainers.PostgreSql;
using Xunit;

namespace Strg.Integration.Tests.Repositories;

/// <summary>
/// <see cref="FileVersion"/> inherits <see cref="Entity"/> rather than <see cref="TenantedEntity"/>,
/// so no global filter scopes the table at the row level. These tests pin the two properties
/// that matter: version ordering on <see cref="FileVersionRepository.ListAsync"/> (newest first,
/// consumers depend on it for "restore previous version" UIs) and that <see cref="FileVersionRepository.AddAsync"/>
/// does not auto-commit — same contract as <see cref="FileRepository"/>.
/// </summary>
public sealed class FileVersionRepositoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task ListAsync_orders_versions_newest_first()
    {
        var tenantId = Guid.NewGuid();
        var options = await CreateFreshDatabaseAsync(tenantId);
        var (driveId, fileId) = await SeedFileAsync(options, tenantId);

        await SeedVersionAsync(options, tenantId, fileId, 1);
        await SeedVersionAsync(options, tenantId, fileId, 2);
        await SeedVersionAsync(options, tenantId, fileId, 3);

        await using var ctx = NewContext(options, tenantId);
        var repo = new FileVersionRepository(ctx);
        var versions = await repo.ListAsync(fileId);

        versions.Select(v => v.VersionNumber).Should().Equal([3, 2, 1]);
    }

    [Fact]
    public async Task GetAsync_returns_null_for_missing_version_number()
    {
        var tenantId = Guid.NewGuid();
        var options = await CreateFreshDatabaseAsync(tenantId);
        var (_, fileId) = await SeedFileAsync(options, tenantId);

        await SeedVersionAsync(options, tenantId, fileId, 1);

        await using var ctx = NewContext(options, tenantId);
        var repo = new FileVersionRepository(ctx);
        var found = await repo.GetAsync(fileId, versionNumber: 99);

        found.Should().BeNull();
    }

    [Fact]
    public async Task AddAsync_does_not_commit_until_caller_saves()
    {
        var tenantId = Guid.NewGuid();
        var options = await CreateFreshDatabaseAsync(tenantId);
        var (_, fileId) = await SeedFileAsync(options, tenantId);

        await using (var ctx = NewContext(options, tenantId))
        {
            var repo = new FileVersionRepository(ctx);
            await repo.AddAsync(new FileVersion
            {
                Id = Guid.NewGuid(),
                FileId = fileId,
                VersionNumber = 1,
                Size = 100,
                ContentHash = "hash-stub",
                StorageKey = "key-stub",
                CreatedBy = Guid.NewGuid(),
            });
            // Deliberately no SaveChangesAsync.
        }

        await using (var ctx = NewContext(options, tenantId))
        {
            var repo = new FileVersionRepository(ctx);
            var versions = await repo.ListAsync(fileId);
            versions.Should().BeEmpty("Add must stay un-committed until the caller saves");
        }
    }

    // --- helpers -------------------------------------------------------------------------------

    private async Task<DbContextOptions<StrgDbContext>> CreateFreshDatabaseAsync(Guid tenantId)
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

        await using (var ctx = new StrgDbContext(options, new TestTenantContext(tenantId)))
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        await using (var ctx = NewContext(options, tenantId))
        {
            ctx.Tenants.Add(new Tenant { Id = tenantId, Name = "seed" });
            await ctx.SaveChangesAsync();
        }

        return options;
    }

    private static async Task<(Guid DriveId, Guid FileId)> SeedFileAsync(DbContextOptions<StrgDbContext> options, Guid tenantId)
    {
        var driveId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        await using var ctx = NewContext(options, tenantId);
        ctx.Drives.Add(new Drive
        {
            Id = driveId,
            TenantId = tenantId,
            Name = $"drive-{driveId:N}",
            ProviderType = "memory",
            ProviderConfig = "{}",
        });
        ctx.Files.Add(new FileItem
        {
            Id = fileId,
            TenantId = tenantId,
            DriveId = driveId,
            Name = "f.txt",
            Path = "f.txt",
            Size = 0,
            CreatedBy = Guid.NewGuid(),
        });
        await ctx.SaveChangesAsync();
        return (driveId, fileId);
    }

    private static async Task SeedVersionAsync(
        DbContextOptions<StrgDbContext> options,
        Guid tenantId,
        Guid fileId,
        int versionNumber)
    {
        await using var ctx = NewContext(options, tenantId);
        ctx.FileVersions.Add(new FileVersion
        {
            Id = Guid.NewGuid(),
            FileId = fileId,
            VersionNumber = versionNumber,
            Size = 10,
            ContentHash = $"hash-{versionNumber}",
            StorageKey = $"key-{versionNumber}",
            CreatedBy = Guid.NewGuid(),
        });
        await ctx.SaveChangesAsync();
    }

    private static StrgDbContext NewContext(DbContextOptions<StrgDbContext> options, Guid tenantId)
        => new(options, new TestTenantContext(tenantId));

    private sealed class TestTenantContext(Guid id) : ITenantContext
    {
        public Guid TenantId => id;
    }
}
