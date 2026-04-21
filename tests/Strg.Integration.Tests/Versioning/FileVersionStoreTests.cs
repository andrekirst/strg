using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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

    // Security-reviewer STRG-043 audit finding I10: cross-tenant Prune must silent-no-op. A tenant B
    // caller passing a fileId belonging to tenant A must not delete rows, must not delete blobs,
    // must not mutate the owner's quota, and must not leak an error that reveals the file's
    // existence in another tenant. The no-op flows from IFileRepository.GetByIdAsync routing
    // through the global tenant filter and returning null — the store's early-return on null keeps
    // the whole operation silent. This test pins that behaviour so a future refactor of the prune
    // entry-point (e.g. replacing the fileRepo lookup with a direct FileVersion scan) cannot
    // regress it invisibly.
    [Fact]
    public async Task PruneVersionsAsync_cross_tenant_is_silent_noop_and_does_not_mutate_owner_quota()
    {
        var fxA = await CreateFixtureAsync();
        var seedA = await fxA.SeedFileAsync(quotaBytes: 100_000_000);
        await fxA.Store.CreateVersionAsync(seedA.File, "blob/cross/v1", Hash("v1"), 100, 120, seedA.UserId, default);
        await fxA.Store.CreateVersionAsync(await fxA.ReloadFileAsync(seedA.File.Id), "blob/cross/v2", Hash("v2"), 200, 220, seedA.UserId, default);
        await fxA.Store.CreateVersionAsync(await fxA.ReloadFileAsync(seedA.File.Id), "blob/cross/v3", Hash("v3"), 300, 320, seedA.UserId, default);
        await fxA.Provider.WriteAsync("blob/cross/v1", new MemoryStream([0x01]));
        await fxA.Provider.WriteAsync("blob/cross/v2", new MemoryStream([0x02]));
        await fxA.Provider.WriteAsync("blob/cross/v3", new MemoryStream([0x03]));

        var fxB = await fxA.WithNewTenantAsync();

        // Act — tenant B attempts to prune tenant A's file. Must not throw (no oracle via
        // exception type) and must silently return.
        var act = () => fxB.Store.PruneVersionsAsync(seedA.File.Id, keepCount: 1, default);
        await act.Should().NotThrowAsync(
            "a cross-tenant Prune is a silent no-op — throwing NotFoundException would leak existence to tenant B");

        // FileVersion rows in tenant A must be intact.
        (await fxA.Store.GetVersionsAsync(seedA.File.Id, default)).Should().HaveCount(3,
            "cross-tenant Prune must NOT delete rows belonging to another tenant");

        // Blobs must be intact — a reaper that bypassed the tenant check would orphan tenant A's
        // ciphertext based on tenant B's call.
        (await fxA.Provider.ExistsAsync("blob/cross/v1")).Should().BeTrue();
        (await fxA.Provider.ExistsAsync("blob/cross/v2")).Should().BeTrue();
        (await fxA.Provider.ExistsAsync("blob/cross/v3")).Should().BeTrue();

        // Quota on tenant A's owner must be exactly what the three successful creates charged
        // (100 + 200 + 300 = 600). Any cross-tenant release would silently grant unlimited quota
        // by letting tenant B credit tenant A's owner.
        (await fxA.ReloadUserAsync(seedA.UserId)).UsedBytes.Should().Be(600,
            "cross-tenant Prune must NOT release quota on another tenant's user");
    }

    // STRG-043 L2 regression gate — per-version atomic tx scope (csharp-implementer commit b753663).
    //
    // PruneVersionsAsync iterates toPrune newest→oldest (versions.Skip(keepCount), where Skip acts on
    // the newest-first list) and wraps each "blob delete + DB row remove + quota release" in its
    // own transaction. Provider.DeleteAsync runs OUTSIDE the per-iteration tx, so a throw at
    // iteration k leaves:
    //   • iterations [0..k-1]: fully committed — blob gone, row gone, quota released
    //   • iteration k: tx was never opened (throw preceded BeginTransactionAsync), so the DB row
    //     and quota charge remain intact; blob state is INDETERMINATE (depends on the provider
    //     implementation's exact throw point — our fake throws before touching storage, but real
    //     providers may have partially committed and then failed on flush)
    //   • iterations [k+1..]: never reached — untouched
    //
    // This test pins that invariant. The failure mode it protects against: a future refactor
    // reverting to "delete all blobs first, then a single DB tx removes all rows + releases sum
    // quota" would leave the DB state unchanged on any mid-loop provider failure (because the
    // single tx never reached SaveChanges) while orphaning [0..k-1]'s blobs on disk with the DB
    // still pointing at them. This test would fail in that shape because it asserts k-1 DB rows
    // are gone + quota partially released — exactly the semantics the single-tx shape cannot
    // produce.
    [Fact]
    public async Task PruneVersionsAsync_partial_failure_commits_pre_throw_work_and_preserves_post_throw_state()
    {
        var fx = await CreateFixtureAsync();
        var seed = await fx.SeedFileAsync(quotaBytes: 100_000_000);

        // Distinct sizes per version → quota arithmetic is unambiguous across release-math
        // errors (equal sizes would mask a release-wrong-version bug).
        await fx.Store.CreateVersionAsync(seed.File, "blob/v1", Hash("v1"), size: 100, blobSizeBytes: 120, seed.UserId, default);
        await fx.Store.CreateVersionAsync(await fx.ReloadFileAsync(seed.File.Id), "blob/v2", Hash("v2"), size: 200, blobSizeBytes: 220, seed.UserId, default);
        await fx.Store.CreateVersionAsync(await fx.ReloadFileAsync(seed.File.Id), "blob/v3", Hash("v3"), size: 300, blobSizeBytes: 320, seed.UserId, default);
        await fx.Store.CreateVersionAsync(await fx.ReloadFileAsync(seed.File.Id), "blob/v4", Hash("v4"), size: 400, blobSizeBytes: 420, seed.UserId, default);
        await fx.Store.CreateVersionAsync(await fx.ReloadFileAsync(seed.File.Id), "blob/v5", Hash("v5"), size: 500, blobSizeBytes: 520, seed.UserId, default);

        await fx.Provider.WriteAsync("blob/v1", new MemoryStream([0x01]));
        await fx.Provider.WriteAsync("blob/v2", new MemoryStream([0x02]));
        await fx.Provider.WriteAsync("blob/v3", new MemoryStream([0x03]));
        await fx.Provider.WriteAsync("blob/v4", new MemoryStream([0x04]));
        await fx.Provider.WriteAsync("blob/v5", new MemoryStream([0x05]));

        // Sanity: starting UsedBytes = 100+200+300+400+500 = 1500.
        (await fx.ReloadUserAsync(seed.UserId)).UsedBytes.Should().Be(1500);

        // toPrune (keepCount=1) is [v4, v3, v2, v1] (newest-first minus the kept v5), so:
        //   iter 1 deletes v4, iter 2 deletes v3, iter 3 throws on v2, iter 4 never runs (v1).
        var failingProvider = new ThrowOnNthDeleteProvider(fx.Provider, throwOnNthCall: 3);
        var store = fx.BuildStore(failingProvider);

        var act = () => store.PruneVersionsAsync(seed.File.Id, keepCount: 1, default);
        var thrown = await act.Should().ThrowAsync<IOException>(
            "the provider failure must bubble unchanged through PruneVersionsAsync — catching it "
            + "would hide partial-failure state from the caller and prevent retry-driven recovery");
        thrown.Which.Message.Should().Contain("call #3");

        failingProvider.DeleteCallCount.Should().Be(3,
            "iter 1 (v4) + iter 2 (v3) delegated to inner; iter 3 (v2) threw before delegating");

        // DB state: iterations [0..1] committed (v4, v3 rows gone), iter 2 (v2) row PRESENT because
        // its tx was never opened, iter 3 (v1) never attempted. Plus the kept v5.
        var remaining = await fx.Store.GetVersionsAsync(seed.File.Id, default);
        remaining.Select(v => v.VersionNumber).Should().ContainInOrder(5, 2, 1);
        remaining.Should().HaveCount(3,
            "surviving VersionNumbers are {1,2,5} — a middle gap at {3,4} is the documented shape of "
            + "a mid-loop failure (see FileVersionStore.PruneVersionsAsync gap-semantics comment); "
            + "retry resumes by re-pruning beyond keepCount=1");

        // Quota: released v4 (400) + v3 (300) = 700; v2's charge (200) remains because its tx did
        // not open. Starting 1500 - 700 released = 800 remaining, which equals sum({v1,v2,v5}) =
        // 100 + 200 + 500. Internal consistency is what a release-wrong-version bug would break.
        (await fx.ReloadUserAsync(seed.UserId)).UsedBytes.Should().Be(800,
            "quota released exactly for the two pre-throw iterations; v2's charge must remain "
            + "intact because its release was inside the same tx the DB row remove lives in");

        // Blobs: v4 + v3 confirmed deleted (pre-throw iterations actually called inner.DeleteAsync);
        // v5 (kept) + v1 (never attempted) intact. v2's blob state is DELIBERATELY not asserted —
        // in this fake it's still there because the wrapper throws before delegating, but a real
        // provider might have torn down part of the storage object before the failure surfaced.
        // Asserting either way would over-constrain the contract.
        (await fx.Provider.ExistsAsync("blob/v4")).Should().BeFalse("iter 1 deleted v4's blob");
        (await fx.Provider.ExistsAsync("blob/v3")).Should().BeFalse("iter 2 deleted v3's blob");
        (await fx.Provider.ExistsAsync("blob/v5")).Should().BeTrue("kept version's blob must remain");
        (await fx.Provider.ExistsAsync("blob/v1")).Should().BeTrue("iter 4 never ran — v1's blob untouched");
        // NO assertion on blob/v2 — indeterminate by contract.
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

        /// <summary>
        /// Builds a store wired against a test-supplied <see cref="IStorageProvider"/> (typically a
        /// wrapping fake like <see cref="ThrowOnNthDeleteProvider"/>). Registers a one-off registry
        /// for the "memory" provider type so <see cref="FileVersionStore"/>'s drive-to-provider
        /// resolution picks up the wrapper. Uses a fresh registry instance per call — never mutates
        /// the fixture's shared <c>_registry</c>, so other stores built from this fixture continue
        /// to see the unwrapped provider.
        /// </summary>
        public IFileVersionStore BuildStore(IStorageProvider customProvider)
        {
            var customRegistry = new StorageProviderRegistry();
            customRegistry.Register("memory", _ => customProvider);
            var db = NewDbContext();
            var versionRepo = new FileVersionRepository(db);
            var fileRepo = new FileRepository(db);
            var driveRepo = new DriveRepository(db);
            var quota = new QuotaService(db, tenantContext, NullLogger<QuotaService>.Instance);
            return new FileVersionStore(db, versionRepo, fileRepo, driveRepo, customRegistry, quota);
        }

        private StrgDbContext NewDbContext() => new(options, tenantContext);

        private IFileVersionStore BuildStore()
        {
            var db = NewDbContext();
            var versionRepo = new FileVersionRepository(db);
            var fileRepo = new FileRepository(db);
            var driveRepo = new DriveRepository(db);
            var quota = new QuotaService(db, tenantContext, NullLogger<QuotaService>.Instance);
            return new FileVersionStore(db, versionRepo, fileRepo, driveRepo, _registry, quota);
        }
    }

    /// <summary>
    /// Test-only <see cref="IStorageProvider"/> wrapper that delegates every call to
    /// <paramref name="inner"/> except <see cref="DeleteAsync"/>, which throws an
    /// <see cref="IOException"/> on the N-th invocation (1-indexed). Lets the L2 partial-failure
    /// test simulate a transient provider outage mid-PruneVersionsAsync without modifying the
    /// real <c>InMemoryStorageProvider</c>. The throw happens BEFORE delegating, so the inner
    /// provider observes exactly <c>throwOnNthCall - 1</c> successful deletes from the wrapper.
    /// </summary>
    private sealed class ThrowOnNthDeleteProvider(IStorageProvider inner, int throwOnNthCall) : IStorageProvider
    {
        private int _deleteCalls;

        public int DeleteCallCount => _deleteCalls;

        public string ProviderType => inner.ProviderType;

        public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
        {
            var n = Interlocked.Increment(ref _deleteCalls);
            if (n == throwOnNthCall)
            {
                throw new IOException($"Simulated provider failure on DeleteAsync call #{n} (path: {path})");
            }
            return inner.DeleteAsync(path, cancellationToken);
        }

        public Task<IStorageFile?> GetFileAsync(string path, CancellationToken cancellationToken = default)
            => inner.GetFileAsync(path, cancellationToken);

        public Task<IStorageDirectory?> GetDirectoryAsync(string path, CancellationToken cancellationToken = default)
            => inner.GetDirectoryAsync(path, cancellationToken);

        public Task<Stream> ReadAsync(string path, long offset = 0, CancellationToken cancellationToken = default)
            => inner.ReadAsync(path, offset, cancellationToken);

        public Task WriteAsync(string path, Stream content, CancellationToken cancellationToken = default)
            => inner.WriteAsync(path, content, cancellationToken);

        public Task MoveAsync(string source, string destination, CancellationToken cancellationToken = default)
            => inner.MoveAsync(source, destination, cancellationToken);

        public Task CopyAsync(string source, string destination, CancellationToken cancellationToken = default)
            => inner.CopyAsync(source, destination, cancellationToken);

        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
            => inner.ExistsAsync(path, cancellationToken);

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
            => inner.CreateDirectoryAsync(path, cancellationToken);

        public IAsyncEnumerable<IStorageItem> ListAsync(string path, CancellationToken cancellationToken = default)
            => inner.ListAsync(path, cancellationToken);
    }
}
