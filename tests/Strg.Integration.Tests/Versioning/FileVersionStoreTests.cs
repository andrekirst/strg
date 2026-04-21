using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Strg.Core.Domain;
using Strg.Core.Exceptions;
using Strg.Core.Services;
using Strg.Core.Storage;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Services;
using Strg.Infrastructure.Storage;
using Strg.Infrastructure.Versioning;
using Testcontainers.PostgreSql;
using Xunit;

namespace Strg.Integration.Tests.Versioning;

internal sealed class FixedTenantContext(Guid id) : ITenantContext
{
    public Guid TenantId => id;
}

/// <summary>
/// Integration tests for <see cref="FileVersionStore"/>. Runs against real Postgres (TestContainers)
/// because the service's hardest invariant — "FileVersion insert + quota commit roll back
/// atomically on failure" — relies on <c>ExecuteUpdateAsync</c> enlisting in the DbContext
/// transaction, and no in-memory provider exercises that contract faithfully.
///
/// <para>Each test gets a fresh database so failures isolate cleanly; the alternative (shared DB
/// with per-test cleanup) collapsed unrelated tests during iterative development of STRG-032.</para>
/// </summary>
public sealed class FileVersionStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    // TC-004: monotonic version numbers.
    [Fact]
    public async Task CreateVersionAsync_starts_at_1_and_monotonically_increments()
    {
        var fx = await CreateFixtureAsync();
        var seed = await fx.SeedFileAsync(quotaBytes: 100_000_000);

        var v1 = await fx.Store.CreateVersionAsync(seed.File, "blob/v1", Hash("v1"), 100, 120, seed.UserId, default);
        var v2 = await fx.Store.CreateVersionAsync(await fx.ReloadFileAsync(seed.File.Id), "blob/v2", Hash("v2"), 200, 220, seed.UserId, default);
        var v3 = await fx.Store.CreateVersionAsync(await fx.ReloadFileAsync(seed.File.Id), "blob/v3", Hash("v3"), 300, 320, seed.UserId, default);

        v1.VersionNumber.Should().Be(1);
        v2.VersionNumber.Should().Be(2);
        v3.VersionNumber.Should().Be(3);
    }

    // AC: FileItem.VersionCount updated.
    [Fact]
    public async Task CreateVersionAsync_updates_FileItem_VersionCount_and_sizes()
    {
        var fx = await CreateFixtureAsync();
        var seed = await fx.SeedFileAsync(quotaBytes: 100_000_000);

        await fx.Store.CreateVersionAsync(seed.File, "blob/v1", Hash("v1"), 111, 130, seed.UserId, default);
        await fx.Store.CreateVersionAsync(await fx.ReloadFileAsync(seed.File.Id), "blob/v2", Hash("v2"), 222, 240, seed.UserId, default);

        var reloaded = await fx.ReloadFileAsync(seed.File.Id);
        reloaded.VersionCount.Should().Be(2);
        reloaded.Size.Should().Be(222, "FileItem.Size tracks the current (latest) version's plaintext size");
        reloaded.ContentHash.Should().Be(Hash("v2"));
        reloaded.StorageKey.Should().Be("blob/v2");
    }

    [Fact]
    public async Task CreateVersionAsync_charges_plaintext_size_against_owner_quota()
    {
        var fx = await CreateFixtureAsync();
        var seed = await fx.SeedFileAsync(quotaBytes: 10_000);

        await fx.Store.CreateVersionAsync(seed.File, "blob/v1", Hash("v1"), size: 1_500, blobSizeBytes: 1_600, seed.UserId, default);

        // Only size (plaintext) moves UsedBytes; blobSizeBytes is not quota-relevant.
        var user = await fx.ReloadUserAsync(seed.UserId);
        user.UsedBytes.Should().Be(1_500);
    }

    [Fact]
    public async Task CreateVersionAsync_rolls_back_row_when_quota_shortfall_throws()
    {
        // Quota 1_000, attempt 1_500: the atomic UPDATE short-circuits before SaveChanges, the
        // transaction rolls back, and there must be ZERO FileVersion rows afterward. A
        // non-transactional implementation (SaveChanges first, Commit second) would leave an
        // orphan row that points at storage the caller thinks they can safely garbage-collect.
        var fx = await CreateFixtureAsync();
        var seed = await fx.SeedFileAsync(quotaBytes: 1_000);

        var act = () => fx.Store.CreateVersionAsync(seed.File, "blob/v1", Hash("v1"), 1_500, 1_600, seed.UserId, default);
        await act.Should().ThrowAsync<QuotaExceededException>();

        var versionCount = await fx.CountFileVersionsAsync(seed.File.Id);
        versionCount.Should().Be(0, "a failed quota commit must roll back the FileVersion insert");

        var reloadedFile = await fx.ReloadFileAsync(seed.File.Id);
        reloadedFile.VersionCount.Should().Be(1, "FileItem.VersionCount starts at 1 by convention and must NOT increment on a failed CreateVersion");

        var reloadedUser = await fx.ReloadUserAsync(seed.UserId);
        reloadedUser.UsedBytes.Should().Be(0, "a rolled-back commit must not leave phantom usage");
    }

    // TC-003
    [Fact]
    public async Task GetVersionAsync_returns_specified_version()
    {
        var fx = await CreateFixtureAsync();
        var seed = await fx.SeedFileAsync(quotaBytes: 100_000_000);
        await fx.Store.CreateVersionAsync(seed.File, "blob/v1", Hash("v1"), 100, 120, seed.UserId, default);
        await fx.Store.CreateVersionAsync(await fx.ReloadFileAsync(seed.File.Id), "blob/v2", Hash("v2"), 200, 220, seed.UserId, default);

        var version = await fx.Store.GetVersionAsync(seed.File.Id, versionNumber: 2, default);

        version.Should().NotBeNull();
        version!.StorageKey.Should().Be("blob/v2");
        version.Size.Should().Be(200);
    }

    [Fact]
    public async Task GetVersionsAsync_returns_versions_newest_first()
    {
        var fx = await CreateFixtureAsync();
        var seed = await fx.SeedFileAsync(quotaBytes: 100_000_000);
        await fx.Store.CreateVersionAsync(seed.File, "blob/v1", Hash("v1"), 100, 120, seed.UserId, default);
        await fx.Store.CreateVersionAsync(await fx.ReloadFileAsync(seed.File.Id), "blob/v2", Hash("v2"), 200, 220, seed.UserId, default);
        await fx.Store.CreateVersionAsync(await fx.ReloadFileAsync(seed.File.Id), "blob/v3", Hash("v3"), 300, 320, seed.UserId, default);

        var versions = await fx.Store.GetVersionsAsync(seed.File.Id, default);

        versions.Select(v => v.VersionNumber).Should().ContainInOrder(3, 2, 1);
    }

    // Security-critical: cross-tenant GetVersionAsync must not leak. The FileVersion table has no
    // tenant filter of its own — the store relies on the FileItem tenant filter. Dropping the
    // upstream fileRepo lookup would turn this into a cross-tenant oracle.
    [Fact]
    public async Task GetVersionAsync_cross_tenant_returns_null()
    {
        var fxA = await CreateFixtureAsync();
        var seedA = await fxA.SeedFileAsync(quotaBytes: 100_000_000);
        await fxA.Store.CreateVersionAsync(seedA.File, "blob/v1", Hash("v1"), 100, 120, seedA.UserId, default);

        var fxB = await fxA.WithNewTenantAsync();

        var version = await fxB.Store.GetVersionAsync(seedA.File.Id, versionNumber: 1, default);

        version.Should().BeNull("a tenant B query must NOT resolve a tenant A file's versions");
    }

    // TC-002 + Code Review Checklist: keepVersions: 1 keeps only the latest.
    [Fact]
    public async Task PruneVersionsAsync_keepCount_1_retains_only_latest()
    {
        var fx = await CreateFixtureAsync();
        var seed = await fx.SeedFileAsync(quotaBytes: 100_000_000);
        await fx.Store.CreateVersionAsync(seed.File, "blob/v1", Hash("v1"), 100, 120, seed.UserId, default);
        await fx.Store.CreateVersionAsync(await fx.ReloadFileAsync(seed.File.Id), "blob/v2", Hash("v2"), 200, 220, seed.UserId, default);
        await fx.Store.CreateVersionAsync(await fx.ReloadFileAsync(seed.File.Id), "blob/v3", Hash("v3"), 300, 320, seed.UserId, default);

        // Seed the storage blobs so we can assert the physical deletes.
        await fx.Provider.WriteAsync("blob/v1", new MemoryStream([0x01]));
        await fx.Provider.WriteAsync("blob/v2", new MemoryStream([0x02]));
        await fx.Provider.WriteAsync("blob/v3", new MemoryStream([0x03]));

        await fx.Store.PruneVersionsAsync(seed.File.Id, keepCount: 1, default);

        var remaining = await fx.Store.GetVersionsAsync(seed.File.Id, default);
        remaining.Should().HaveCount(1);
        remaining[0].VersionNumber.Should().Be(3);

        (await fx.Provider.ExistsAsync("blob/v1")).Should().BeFalse();
        (await fx.Provider.ExistsAsync("blob/v2")).Should().BeFalse();
        (await fx.Provider.ExistsAsync("blob/v3")).Should().BeTrue("kept version's blob must remain");
    }

    // Spec Code Review Checklist: keepVersions: 0 means "keep all", not "delete all".
    [Fact]
    public async Task PruneVersionsAsync_keepCount_0_keeps_all_versions()
    {
        var fx = await CreateFixtureAsync();
        var seed = await fx.SeedFileAsync(quotaBytes: 100_000_000);
        await fx.Store.CreateVersionAsync(seed.File, "blob/v1", Hash("v1"), 100, 120, seed.UserId, default);
        await fx.Store.CreateVersionAsync(await fx.ReloadFileAsync(seed.File.Id), "blob/v2", Hash("v2"), 200, 220, seed.UserId, default);
        await fx.Provider.WriteAsync("blob/v1", new MemoryStream([0x01]));
        await fx.Provider.WriteAsync("blob/v2", new MemoryStream([0x02]));

        await fx.Store.PruneVersionsAsync(seed.File.Id, keepCount: 0, default);

        var remaining = await fx.Store.GetVersionsAsync(seed.File.Id, default);
        remaining.Should().HaveCount(2, "keepCount:0 means retention disabled, not mass-delete");
        (await fx.Provider.ExistsAsync("blob/v1")).Should().BeTrue();
        (await fx.Provider.ExistsAsync("blob/v2")).Should().BeTrue();
    }

    [Fact]
    public async Task PruneVersionsAsync_releases_quota_for_pruned_versions()
    {
        var fx = await CreateFixtureAsync();
        var seed = await fx.SeedFileAsync(quotaBytes: 100_000_000);
        await fx.Store.CreateVersionAsync(seed.File, "blob/v1", Hash("v1"), 100, 120, seed.UserId, default);
        await fx.Store.CreateVersionAsync(await fx.ReloadFileAsync(seed.File.Id), "blob/v2", Hash("v2"), 200, 220, seed.UserId, default);
        await fx.Store.CreateVersionAsync(await fx.ReloadFileAsync(seed.File.Id), "blob/v3", Hash("v3"), 300, 320, seed.UserId, default);
        await fx.Provider.WriteAsync("blob/v1", new MemoryStream([0x01]));
        await fx.Provider.WriteAsync("blob/v2", new MemoryStream([0x02]));
        await fx.Provider.WriteAsync("blob/v3", new MemoryStream([0x03]));

        // Before prune: UsedBytes = 100 + 200 + 300 = 600.
        (await fx.ReloadUserAsync(seed.UserId)).UsedBytes.Should().Be(600);

        await fx.Store.PruneVersionsAsync(seed.File.Id, keepCount: 1, default);

        var user = await fx.ReloadUserAsync(seed.UserId);
        user.UsedBytes.Should().Be(300, "pruning v1 (100) + v2 (200) releases 300 bytes; v3 (300) remains");
    }

    [Fact]
    public async Task PruneVersionsAsync_when_existing_count_within_keep_limit_is_noop()
    {
        var fx = await CreateFixtureAsync();
        var seed = await fx.SeedFileAsync(quotaBytes: 100_000_000);
        await fx.Store.CreateVersionAsync(seed.File, "blob/v1", Hash("v1"), 100, 120, seed.UserId, default);
        await fx.Store.CreateVersionAsync(await fx.ReloadFileAsync(seed.File.Id), "blob/v2", Hash("v2"), 200, 220, seed.UserId, default);
        await fx.Provider.WriteAsync("blob/v1", new MemoryStream([0x01]));
        await fx.Provider.WriteAsync("blob/v2", new MemoryStream([0x02]));

        await fx.Store.PruneVersionsAsync(seed.File.Id, keepCount: 5, default);

        (await fx.Store.GetVersionsAsync(seed.File.Id, default)).Should().HaveCount(2);
        (await fx.Provider.ExistsAsync("blob/v1")).Should().BeTrue();
        (await fx.Provider.ExistsAsync("blob/v2")).Should().BeTrue();
    }

    // TC-002 extended: 5 versions with keepCount 3 → oldest 2 pruned.
    [Fact]
    public async Task PruneVersionsAsync_keepCount_3_prunes_oldest_when_five_versions_exist()
    {
        var fx = await CreateFixtureAsync();
        var seed = await fx.SeedFileAsync(quotaBytes: 100_000_000);
        for (var i = 1; i <= 5; i++)
        {
            var file = i == 1 ? seed.File : await fx.ReloadFileAsync(seed.File.Id);
            await fx.Store.CreateVersionAsync(file, $"blob/v{i}", Hash($"v{i}"), 10, 12, seed.UserId, default);
            await fx.Provider.WriteAsync($"blob/v{i}", new MemoryStream([(byte)i]));
        }

        await fx.Store.PruneVersionsAsync(seed.File.Id, keepCount: 3, default);

        var remaining = await fx.Store.GetVersionsAsync(seed.File.Id, default);
        remaining.Select(v => v.VersionNumber).Should().ContainInOrder(5, 4, 3);
        (await fx.Provider.ExistsAsync("blob/v1")).Should().BeFalse();
        (await fx.Provider.ExistsAsync("blob/v2")).Should().BeFalse();
        (await fx.Provider.ExistsAsync("blob/v3")).Should().BeTrue();
    }

    private static string Hash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ── fixture helpers ────────────────────────────────────────────────────────

    private async Task<Fixture> CreateFixtureAsync()
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

        var tenantId = Guid.NewGuid();
        var tenantContext = new FixedTenantContext(tenantId);

        await using (var bootstrap = new StrgDbContext(options, tenantContext))
        {
            await bootstrap.Database.EnsureCreatedAsync();
            bootstrap.Tenants.Add(new Tenant { Id = tenantId, Name = $"test-{tenantId:N}" });
            await bootstrap.SaveChangesAsync();
        }

        // One InMemoryStorageProvider per fixture, shared across all DbContext instances. The
        // factory registered below returns this single instance so prune-path deletes and
        // seed-path writes land in the same store.
        var provider = new InMemoryStorageProvider();
        var registry = new StorageProviderRegistry();
        registry.Register("memory", _ => provider);

        return new Fixture(options, tenantContext, tenantId, registry, provider);
    }

    private sealed record Seed(FileItem File, Guid UserId);

    private sealed class Fixture(
        DbContextOptions<StrgDbContext> options,
        ITenantContext tenantContext,
        Guid tenantId,
        IStorageProviderRegistry registry,
        InMemoryStorageProvider provider)
    {
        private readonly IStorageProviderRegistry _registry = registry;
        private readonly InMemoryStorageProvider _provider = provider;

        public Guid TenantId { get; } = tenantId;
        public InMemoryStorageProvider Provider => _provider;

        /// <summary>
        /// Builds a freshly wired store on a fresh DbContext. Each call is independent — intentional,
        /// so tests that call the store twice exercise the real "tracked state may be stale across
        /// contexts" contract an upload handler experiences in production.
        /// </summary>
        public IFileVersionStore Store => BuildStore();

        public async Task<Seed> SeedFileAsync(long quotaBytes)
        {
            await using var ctx = NewDbContext();
            var user = new User
            {
                TenantId = TenantId,
                Email = $"owner-{Guid.NewGuid():N}@example.com",
                DisplayName = "Owner",
                PasswordHash = "not-a-real-hash-tests-only",
                QuotaBytes = quotaBytes,
                UsedBytes = 0,
            };
            ctx.Users.Add(user);

            var drive = new Drive
            {
                TenantId = TenantId,
                Name = $"drive-{Guid.NewGuid():N}",
                ProviderType = "memory",
                ProviderConfig = "{}",
            };
            ctx.Drives.Add(drive);

            var file = new FileItem
            {
                TenantId = TenantId,
                DriveId = drive.Id,
                Name = "doc.txt",
                Path = "/doc.txt",
                CreatedBy = user.Id,
            };
            ctx.Files.Add(file);

            await ctx.SaveChangesAsync();
            return new Seed(file, user.Id);
        }

        public async Task<FileItem> ReloadFileAsync(Guid fileId)
        {
            await using var ctx = NewDbContext();
            var file = await ctx.Files.FirstOrDefaultAsync(f => f.Id == fileId);
            file.Should().NotBeNull($"file {fileId} should exist for reload");
            return file!;
        }

        public async Task<User> ReloadUserAsync(Guid userId)
        {
            await using var ctx = NewDbContext();
            var user = await ctx.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId);
            user.Should().NotBeNull($"user {userId} should exist for reload");
            return user!;
        }

        public async Task<int> CountFileVersionsAsync(Guid fileId)
        {
            await using var ctx = NewDbContext();
            return await ctx.FileVersions.CountAsync(v => v.FileId == fileId);
        }

        public async Task<Fixture> WithNewTenantAsync()
        {
            var newTenantId = Guid.NewGuid();
            var newTenantContext = new FixedTenantContext(newTenantId);
            await using var ctx = new StrgDbContext(options, newTenantContext);
            ctx.Tenants.Add(new Tenant { Id = newTenantId, Name = $"test-{newTenantId:N}" });
            await ctx.SaveChangesAsync();
            return new Fixture(options, newTenantContext, newTenantId, _registry, _provider);
        }

        private StrgDbContext NewDbContext() => new(options, tenantContext);

        private IFileVersionStore BuildStore()
        {
            var db = NewDbContext();
            var versionRepo = new FileVersionRepository(db);
            var fileRepo = new FileRepository(db);
            var driveRepo = new DriveRepository(db);
            var quota = new QuotaService(db);
            return new FileVersionStore(db, versionRepo, fileRepo, driveRepo, _registry, quota);
        }
    }
}
