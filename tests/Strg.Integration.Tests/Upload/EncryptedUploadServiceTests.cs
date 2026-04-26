using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Strg.Core.Domain;
using Strg.Core.Exceptions;
using Strg.Core.Services;
using Strg.Core.Storage;
using Strg.Infrastructure.Auditing;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Services;
using Strg.Infrastructure.Storage;
using Strg.Infrastructure.Storage.Encryption;
using Strg.Infrastructure.Versioning;
using Testcontainers.PostgreSql;
using Xunit;

namespace Strg.Integration.Tests.Upload;

internal sealed class FixedTenantContext(Guid id) : ITenantContext
{
    public Guid TenantId => id;
}

/// <summary>
/// Integration tests for encrypted-upload orchestration (team-lead Task #40 / STRG-026 devils-advocate
/// critique #2). Proves the <see cref="IEncryptingFileWriter"/> contract that production upload
/// services (STRG-032 / STRG-033) will have to satisfy: "on DB failure after storage success, the
/// orphan ciphertext is reaped by the purge job".
///
/// <para><b>Why the stub.</b> <see cref="NaiveEncryptedUploadService"/> composes
/// <see cref="IEncryptingFileWriter"/> and <see cref="IFileVersionStore"/> in the obvious
/// blob-first-then-DB order — exactly the order that makes orphans possible. Landing a
/// half-finished production upload service in <c>src/</c> would violate CLAUDE.md's no-half-finished
/// rule; a test-project stub pins the contract without that cost.</para>
///
/// <para><b>The two satisfying protocols.</b> When STRG-032 / STRG-033 lands a production upload
/// service, it will choose ONE of:
/// <list type="bullet">
/// <item><b>Orphan reaper.</b> A sweep that scans for blobs-without-matching-FileVersion and
///   deletes them on a schedule. NOT what STRG-036 sweeps today — STRG-036 as designed only
///   purges soft-deleted FileItems (<c>docs/issues/strg/STRG-036-soft-delete-purge-job.md</c>
///   lines 117-126). A blob-orphan reaper is a NEW sweep type.</item>
/// <item><b>Two-phase upload.</b> Writer stages the ciphertext at a temporary key, the DB
///   transaction commits the FileVersion row, and only then a promote-step moves the blob to
///   its final StorageKey. A short-window sweep collects temp keys whose owning tx rolled back.</item>
/// </list>
/// STRG-034's TUS upload endpoint landed with the two-phase temp→promote protocol — see
/// <c>StrgTusUploadTests.TC003_Quota_exceeded_at_complete_returns_413_no_rows_no_final_blob_temp_cleaned</c>
/// for the production-path regression gate (assertion polarity .BeFalse() — no orphan blob).</para>
///
/// <para><b>Second orphan shape (security-reviewer STRG-043 audit I8).</b> Soft-deleted FileItems
/// leak BOTH their ciphertext blobs AND the quota charge on the owner until the reaper runs.
/// <see cref="FileVersionStore.PruneVersionsAsync"/> silent-no-ops on soft-deleted files because
/// <see cref="IFileRepository.GetByIdAsync"/> returns null through the global soft-delete filter;
/// that is correct at the prune entry-point but leaves the reaper as the sole recovery path.
/// <see cref="Soft_deleted_FileItem_leaves_ciphertext_blob_and_quota_charged_TODO_reaper_I8"/>
/// pins that gap — the same reaper that closes the storage-orphan gap above must ALSO release
/// quota + delete blobs for versions whose owning FileItem is soft-deleted.</para>
/// </summary>
public sealed class EncryptedUploadServiceTests : IAsyncLifetime
{
    // Any 32 deterministic bytes; the tests don't exercise KEK security, only orchestration.
    private const string ValidKekBase64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Upload_happy_path_encrypts_blob_and_creates_FileVersion_and_charges_quota()
    {
        var fx = await CreateFixtureAsync();
        var seed = await fx.SeedFileAsync(quotaBytes: 10_000_000);
        var uploadService = fx.BuildUploadService();

        var plaintext = "hello strg"u8.ToArray();

        var version = await uploadService.UploadAsync(seed.File, plaintext, seed.UserId);

        version.VersionNumber.Should().Be(1);
        version.Size.Should().Be(plaintext.Length, "FileVersion.Size is plaintext-denominated");
        version.StorageKey.Should().Be(uploadService.LastStorageKey);
        version.BlobSizeBytes.Should().BeGreaterThan(plaintext.Length,
            "envelope (header + tag) adds overhead so on-disk size exceeds plaintext");

        (await fx.Provider.ExistsAsync(version.StorageKey)).Should()
            .BeTrue("the writer commits ciphertext during the upload");

        (await fx.Store.GetVersionsAsync(seed.File.Id)).Should().ContainSingle();

        (await fx.ReloadUserAsync(seed.UserId)).UsedBytes.Should().Be(plaintext.Length,
            "quota is plaintext-denominated (devils-advocate STRG-026 #5) — envelope overhead is off-books");
    }

    [Fact]
    public async Task Upload_failure_on_quota_rolls_back_version_row_and_quota_charge()
    {
        // Pins the DB-side half of the orphan-reaping contract that IS already met: when
        // CreateVersionAsync's inner transaction rolls back (quota shortfall via the atomic
        // ExecuteUpdateAsync gate), BOTH the FileVersion insert AND the UsedBytes UPDATE revert
        // together. A future refactor that moved the quota charge outside CreateVersionAsync
        // would turn that atomic rollback into a best-effort promise — this test would catch it.
        var fx = await CreateFixtureAsync();
        var seed = await fx.SeedFileAsync(quotaBytes: 50);
        var uploadService = fx.BuildUploadService();

        var plaintext = new byte[100];

        var act = () => uploadService.UploadAsync(seed.File, plaintext, seed.UserId);
        await act.Should().ThrowAsync<QuotaExceededException>();

        (await fx.Store.GetVersionsAsync(seed.File.Id)).Should().BeEmpty(
            "transactional rollback must leave zero FileVersion rows for the failed upload");
        (await fx.ReloadUserAsync(seed.UserId)).UsedBytes.Should().Be(0,
            "quota UPDATE rolls back in the same tx the FileVersion insert lives in");
    }

    // STRG-034 polarity-flip: the regression gate that pinned the orphan-ciphertext gap on the
    // NaiveEncryptedUploadService path moved to the production TUS endpoint when STRG-034 landed.
    // The .BeFalse() assertion now lives in StrgTusUploadTests.TC003, which exercises the full
    // two-phase orchestrator (temp-key write → DB commit → MoveAsync promote) and asserts that a
    // quota-rejected upload leaves NO orphan ciphertext at the temp key. NaiveEncryptedUploadService
    // is kept alive only for the lower-level IEncryptingFileWriter + IFileVersionStore contract
    // pinned by the two surviving tests above (happy path, tx rollback).

    [Fact]
    public async Task Soft_deleted_FileItem_leaves_ciphertext_blob_and_quota_charged_TODO_reaper_I8()
    {
        // REGRESSION GATE — security-reviewer STRG-043 audit finding I8.
        //
        // A successful upload produces: ciphertext blob on disk + FileVersion row + quota charge on
        // the owner. When the owning FileItem is later soft-deleted:
        //   • FileVersion rows are NOT cascaded-deleted (soft-delete on FileItem only flips
        //     DeletedAt; rows on child tables remain untouched by design — hard-cascade would
        //     defeat the 30-day undelete window).
        //   • The blob stays on disk because nothing sweeps it.
        //   • UsedBytes on the owner stays charged — there is no release path on soft-delete.
        //   • FileVersionStore.PruneVersionsAsync silent-no-ops for the soft-deleted FileItem
        //     because IFileRepository.GetByIdAsync routes through the global filter and returns
        //     null. That no-op is correct at the prune entry-point (caller has a stale fileId) but
        //     removes prune as a recovery path — only the reaper can close this gap.
        //
        // The same reaper that closes the blob-orphan gap in the previous test MUST also cover
        // this shape: blobs whose FileVersion row points at a soft-deleted FileItem. Missing it
        // leaks quota permanently (until 30-day hard-delete runs, and only if THAT job remembers
        // to release quota — STRG-036 as designed does not).
        //
        // Flip BOTH `.BeTrue()` assertions to `.BeFalse()` and drop the _TODO_reaper_I8 suffix
        // when the reaper is wired to release quota + delete blob for soft-deleted-FileItem
        // versions. Delete this block comment at the same time.
        var fx = await CreateFixtureAsync();
        var seed = await fx.SeedFileAsync(quotaBytes: 10_000_000);
        var uploadService = fx.BuildUploadService();
        var plaintext = "hello strg"u8.ToArray();

        var version = await uploadService.UploadAsync(seed.File, plaintext, seed.UserId);

        (await fx.ReloadUserAsync(seed.UserId)).UsedBytes.Should().Be(plaintext.Length,
            "sanity: happy-path charged UsedBytes so the post-soft-delete assertion is meaningful");

        await fx.SoftDeleteFileAsync(seed.File.Id);

        (await fx.Provider.ExistsAsync(version.StorageKey)).Should().BeTrue(
            "TODO STRG-043 I8: soft-delete leaves blob on disk today; flip to BeFalse when reaper sweeps "
            + "blobs whose FileVersion.File.DeletedAt IS NOT NULL");
        (await fx.ReloadUserAsync(seed.UserId)).UsedBytes.Should().Be(plaintext.Length,
            "TODO STRG-043 I8: soft-delete does not release quota today; flip the expected value to 0 "
            + "when reaper releases quota alongside blob-delete for soft-deleted-FileItem versions");
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

        // One InMemoryStorageProvider per fixture, shared across DbContext instances AND the
        // encrypting writer — the writer's "inner" provider must be the same instance the
        // orchestrator queries for blob existence, otherwise the orphan-blob assertion is
        // observing a different store than the one the writer wrote to.
        var provider = new InMemoryStorageProvider();
        var registry = new StorageProviderRegistry();
        registry.Register("memory", _ => provider);

        var keyProvider = new EnvVarKeyProvider(ValidKekBase64);
        var encryptingWriter = new AesGcmFileWriter(provider, keyProvider);

        return new Fixture(options, tenantContext, tenantId, registry, provider, encryptingWriter);
    }

    private sealed record Seed(FileItem File, Guid UserId);

    private sealed class Fixture(
        DbContextOptions<StrgDbContext> options,
        ITenantContext tenantContext,
        Guid tenantId,
        IStorageProviderRegistry registry,
        InMemoryStorageProvider provider,
        IEncryptingFileWriter encryptingWriter)
    {
        public Guid TenantId { get; } = tenantId;
        public InMemoryStorageProvider Provider => provider;

        /// <summary>
        /// Fresh <see cref="IFileVersionStore"/> on a fresh <see cref="StrgDbContext"/> per access.
        /// Matches <c>FileVersionStoreTests</c> — intentional so tests that read state between
        /// operations observe committed rows, not tracker-cached staged ones.
        /// </summary>
        public IFileVersionStore Store => BuildStore();

        public NaiveEncryptedUploadService BuildUploadService() =>
            new(encryptingWriter, BuildStore(), provider);

        public async Task<Seed> SeedFileAsync(long quotaBytes)
        {
            await using var ctx = NewDbContext();
            var user = new User
            {
                TenantId = TenantId,
                Email = $"owner-{Guid.NewGuid():N}@example.com",
                DisplayName = "Upload Owner",
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
                Name = "encrypted.bin",
                Path = "/encrypted.bin",
                CreatedBy = user.Id,
            };
            ctx.Files.Add(file);

            await ctx.SaveChangesAsync();
            return new Seed(file, user.Id);
        }

        public async Task<User> ReloadUserAsync(Guid userId)
        {
            await using var ctx = NewDbContext();
            var user = await ctx.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId);
            user.Should().NotBeNull($"user {userId} should exist for reload");
            return user!;
        }

        /// <summary>
        /// Soft-deletes a <see cref="FileItem"/> by stamping <see cref="TenantedEntity.DeletedAt"/>.
        /// Post-delete, the global filter hides the row from normal queries — exactly the state
        /// that makes <c>PruneVersionsAsync</c> silent-no-op on the file (see I8 test xmldoc for
        /// why that no-op is correct but leaves the reaper as the sole recovery path).
        /// </summary>
        public async Task SoftDeleteFileAsync(Guid fileId)
        {
            await using var ctx = NewDbContext();
            var file = await ctx.Files.FirstOrDefaultAsync(f => f.Id == fileId);
            file.Should().NotBeNull($"file {fileId} should exist prior to soft-delete");
            file!.DeletedAt = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync();
        }

        private StrgDbContext NewDbContext() => new(options, tenantContext);

        private IFileVersionStore BuildStore()
        {
            var db = NewDbContext();
            var versionRepo = new FileVersionRepository(db);
            var fileRepo = new FileRepository(db);
            var driveRepo = new DriveRepository(db);
            var quota = new QuotaService(db, tenantContext, new Quota.CapturingPublishEndpoint(), NullLogger<QuotaService>.Instance);
            var audit = new AuditService(db);
            return new FileVersionStore(db, versionRepo, fileRepo, driveRepo, registry, quota, audit, NullLogger<FileVersionStore>.Instance);
        }
    }
}
