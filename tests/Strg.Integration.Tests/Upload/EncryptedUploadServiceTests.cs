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
/// Neither exists at HEAD. <see cref="Upload_failure_on_quota_orphans_ciphertext_blob_TODO_STRG026_hash2"/>
/// is the regression gate — its assertion polarity flips when either protocol lands.</para>
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

        (await fx.Store.GetVersionsAsync(seed.File.Id, default)).Should().ContainSingle();

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

        (await fx.Store.GetVersionsAsync(seed.File.Id, default)).Should().BeEmpty(
            "transactional rollback must leave zero FileVersion rows for the failed upload");
        (await fx.ReloadUserAsync(seed.UserId)).UsedBytes.Should().Be(0,
            "quota UPDATE rolls back in the same tx the FileVersion insert lives in");
    }

    [Fact]
    public async Task Upload_failure_on_quota_orphans_ciphertext_blob_TODO_STRG026_hash2()
    {
        // REGRESSION GATE — codifies the orphan-ciphertext gap STRG-026 devils-advocate #2 tracks.
        //
        // Today: IEncryptingFileWriter.WriteAsync commits the blob to storage BEFORE
        // IFileVersionStore.CreateVersionAsync opens its transaction. When that transaction fails
        // (quota here; could equally be a unique-index race on (FileId, VersionNumber), DB
        // connectivity loss mid-SaveChanges, a killed process, etc.), the DB state rolls back
        // cleanly but the blob persists on disk with no FileVersion row pointing at it — an
        // unreachable leak that only a full-scan reaper can collect.
        //
        // STRG-036 as designed sweeps soft-deleted FileItems only
        // (docs/issues/strg/STRG-036-soft-delete-purge-job.md, lines 117-126). It does NOT scan
        // for blobs-without-matching-FileVersion. That's a separate sweep type — or alternatively
        // a two-phase upload protocol (temp key → promote on tx-commit) prevents the orphan from
        // ever reaching the final key space.
        //
        // When STRG-032 / STRG-033 lands one of those protocols:
        //   1. Flip the assertion below from BeTrue() to BeFalse().
        //   2. Rename this test to drop the _TODO_ prefix (e.g. _reaps_or_prevents_orphan).
        //   3. Delete this block comment (the fix will make it stale).
        // The red-now / green-after-fix polarity is the whole point: the fix author CANNOT
        // merge without updating the test, which is the only way to prevent a silent regression.
        var fx = await CreateFixtureAsync();
        var seed = await fx.SeedFileAsync(quotaBytes: 50);
        var uploadService = fx.BuildUploadService();

        var plaintext = new byte[100];

        try
        {
            await uploadService.UploadAsync(seed.File, plaintext, seed.UserId);
        }
        catch (QuotaExceededException)
        {
            // Expected — the failure path is exactly what we're here to observe.
        }

        uploadService.LastStorageKey.Should().NotBeEmpty("the writer must have run before the DB tx failed");
        (await fx.Provider.ExistsAsync(uploadService.LastStorageKey)).Should().BeTrue(
            "orphan ciphertext persists today because the writer commits before the DB tx — STRG-026 #2 gap; "
            + "flip this to BeFalse when a blob-without-matching-FileVersion reaper OR a two-phase upload "
            + "protocol lands, and update the test name + block comment above to match");
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

        private StrgDbContext NewDbContext() => new(options, tenantContext);

        private IFileVersionStore BuildStore()
        {
            var db = NewDbContext();
            var versionRepo = new FileVersionRepository(db);
            var fileRepo = new FileRepository(db);
            var driveRepo = new DriveRepository(db);
            var quota = new QuotaService(db, tenantContext, NullLogger<QuotaService>.Instance);
            return new FileVersionStore(db, versionRepo, fileRepo, driveRepo, registry, quota);
        }
    }
}
