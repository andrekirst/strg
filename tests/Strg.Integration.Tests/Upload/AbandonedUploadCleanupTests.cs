using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Strg.Core.Constants;
using Strg.Core.Domain;
using Strg.Core.Storage;
using Strg.Infrastructure.BackgroundJobs;
using Strg.Infrastructure.Storage;
using Strg.Infrastructure.Upload;
using Xunit;

namespace Strg.Integration.Tests.Upload;

/// <summary>
/// STRG-035 — abandoned TUS upload cleanup. The cleanup job is constructed in-test from the
/// host's DI surface and driven via its <c>internal</c> <c>RunOnceAsync</c> seam (exposed via
/// <c>InternalsVisibleTo("Strg.Integration.Tests")</c>) so each test executes a single deterministic
/// sweep instead of polling the host's 5-minute timer.
///
/// <para>The four tests share <see cref="NoSweepFixture"/>: a sub-fixture of
/// <see cref="StrgTusUploadFixture"/> that strips the production
/// <see cref="AbandonedUploadCleanupJob"/> hosted service so its background loop cannot race the
/// deterministic <c>RunOnceAsync</c> seam these tests exercise. The production registration is
/// covered indirectly by the full integration suite.</para>
/// </summary>
public sealed class AbandonedUploadCleanupTests(AbandonedUploadCleanupTests.NoSweepFixture fx)
    : IClassFixture<AbandonedUploadCleanupTests.NoSweepFixture>
{
    private readonly NoSweepFixture _fx = fx;

    [Fact]
    public async Task TC001_Single_expired_upload_is_swept_DB_row_and_temp_blob_gone()
    {
        var (uploadId, tempStorageKey) = await SeedExpiredUploadAsync(
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            isCompleted: false,
            writeTempBlob: true);

        var beforeUsedBytes = await _fx.ReadUsedBytesAsync();

        var swept = await BuildJob().RunOnceAsync(CancellationToken.None);

        swept.Should().BeGreaterThanOrEqualTo(1);

        await using var ctx = _fx.NewDbContext();
        (await ctx.PendingUploads.AnyAsync(p => p.UploadId == uploadId))
            .Should().BeFalse("the abandoned row was reaped");

        File.Exists(Path.Combine(_fx.TempStorageRoot, tempStorageKey))
            .Should().BeFalse("the temp blob was deleted");
        File.Exists(Path.Combine(_fx.TempStorageRoot, tempStorageKey + ".part"))
            .Should().BeFalse("the .part sidecar was deleted");

        // STRG-035 plan Decision A1: abandoned uploads never committed quota, so the cleanup
        // job does NOT call IQuotaService.ReleaseAsync. UsedBytes must therefore be unchanged.
        (await _fx.ReadUsedBytesAsync()).Should().Be(beforeUsedBytes,
            "abandoned uploads (IsCompleted=false) never committed quota — see STRG-035 Decision A1");
    }

    [Fact]
    public async Task TC002_Completed_upload_is_NOT_swept()
    {
        // IsCompleted=true rows belong to the phase-3 inversion recovery path — out of scope for STRG-035.
        var (uploadId, _) = await SeedExpiredUploadAsync(
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            isCompleted: true,
            writeTempBlob: false);

        await BuildJob().RunOnceAsync(CancellationToken.None);

        await using var ctx = _fx.NewDbContext();
        (await ctx.PendingUploads.AnyAsync(p => p.UploadId == uploadId))
            .Should().BeTrue("IsCompleted=true rows are reserved for the phase-3 recovery path; STRG-035 must skip them");
    }

    [Fact]
    public async Task TC003_Multiple_expired_uploads_all_swept_in_single_run()
    {
        var seeded = new List<(Guid uploadId, string tempKey)>();
        for (var i = 0; i < 3; i++)
        {
            seeded.Add(await SeedExpiredUploadAsync(
                expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1),
                isCompleted: false,
                writeTempBlob: true));
        }

        var swept = await BuildJob().RunOnceAsync(CancellationToken.None);

        swept.Should().BeGreaterThanOrEqualTo(3);

        await using var ctx = _fx.NewDbContext();
        var seededIds = seeded.Select(s => s.uploadId).ToHashSet();
        (await ctx.PendingUploads.Where(p => seededIds.Contains(p.UploadId)).ToListAsync())
            .Should().BeEmpty();

        foreach (var (_, tempKey) in seeded)
        {
            File.Exists(Path.Combine(_fx.TempStorageRoot, tempKey)).Should().BeFalse();
            File.Exists(Path.Combine(_fx.TempStorageRoot, tempKey + ".part")).Should().BeFalse();
        }
    }

    [Fact]
    public async Task TC004_Storage_delete_failure_logs_and_continues_DB_row_still_removed()
    {
        var seeded = new[]
        {
            await SeedExpiredUploadAsync(
                expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1),
                isCompleted: false,
                writeTempBlob: true),
            await SeedExpiredUploadAsync(
                expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1),
                isCompleted: false,
                writeTempBlob: true),
        };

        // Build a hostile registry whose resolved provider throws on DeleteAsync. Mirrors the
        // StrgTusPhase3InversionTests.MoveFailingFixture pattern (lines 83-105) but injected
        // per-test rather than per-fixture so we don't pay for a second testcontainer pair.
        var failingRegistry = new StorageProviderRegistry();
        failingRegistry.Register("local", config =>
        {
            var rootPath = config.GetValue<string>("rootPath")
                ?? throw new InvalidOperationException("'local' requires rootPath");
            return new DeleteFailingProvider(new LocalFileSystemProvider(rootPath));
        });

        var job = new AbandonedUploadCleanupJob(
            _fx.Services.GetRequiredService<IServiceScopeFactory>(),
            failingRegistry,
            _fx.Services.GetRequiredService<TimeProvider>(),
            _fx.Services.GetRequiredService<IOptions<StrgTusOptions>>(),
            _fx.Services.GetRequiredService<ILogger<AbandonedUploadCleanupJob>>());

        // Must not throw — Decision E1: per-upload errors are logged and the loop continues.
        var swept = await job.RunOnceAsync(CancellationToken.None);
        swept.Should().BeGreaterThanOrEqualTo(2);

        await using var ctx = _fx.NewDbContext();
        var seededIds = seeded.Select(s => s.uploadId).ToHashSet();
        (await ctx.PendingUploads.Where(p => seededIds.Contains(p.UploadId)).ToListAsync())
            .Should().BeEmpty("Decision E1: row removal is unconditional even when blob delete throws");

        // The blobs are STILL on disk because Delete threw — the orphan blob is the trade-off
        // documented in the cleanup job's class summary (a permanently-broken storage path
        // mustn't loop forever).
        foreach (var (_, tempKey) in seeded)
        {
            File.Exists(Path.Combine(_fx.TempStorageRoot, tempKey))
                .Should().BeTrue("Decision E1: Delete threw, so the temp blob remains");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<(Guid uploadId, string tempStorageKey)> SeedExpiredUploadAsync(
        DateTimeOffset expiresAt,
        bool isCompleted,
        bool writeTempBlob)
    {
        var uploadId = Guid.NewGuid();
        var tempKey = StrgUploadKeys.TempKey(_fx.DriveId, uploadId);

        await using (var ctx = _fx.NewDbContext())
        {
            ctx.PendingUploads.Add(new PendingUpload
            {
                TenantId = _fx.TenantId,
                UploadId = uploadId,
                DriveId = _fx.DriveId,
                UserId = _fx.UserId,
                Path = $"sweep-test/{uploadId:N}.bin",
                Filename = $"{uploadId:N}.bin",
                MimeType = "application/octet-stream",
                DeclaredSize = 100,
                ExpiresAt = expiresAt,
                TempStorageKey = tempKey,
                IsCompleted = isCompleted,
            });
            await ctx.SaveChangesAsync();
        }

        if (writeTempBlob)
        {
            var fullPath = Path.Combine(_fx.TempStorageRoot, tempKey);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllBytesAsync(fullPath, new byte[100]);
            await File.WriteAllBytesAsync(fullPath + ".part", new byte[100]);
        }

        return (uploadId, tempKey);
    }

    private AbandonedUploadCleanupJob BuildJob() =>
        new(
            _fx.Services.GetRequiredService<IServiceScopeFactory>(),
            _fx.Services.GetRequiredService<IStorageProviderRegistry>(),
            _fx.Services.GetRequiredService<TimeProvider>(),
            _fx.Services.GetRequiredService<IOptions<StrgTusOptions>>(),
            _fx.Services.GetRequiredService<ILogger<AbandonedUploadCleanupJob>>());

    /// <summary>
    /// Strips the production <see cref="AbandonedUploadCleanupJob"/> hosted-service registration
    /// from the host's DI container. <see cref="Microsoft.Extensions.Hosting.BackgroundService.StartAsync"/>
    /// kicks off <see cref="Microsoft.Extensions.Hosting.BackgroundService.ExecuteAsync"/> on a
    /// thread-pool thread without awaiting it, so the host's first sweep iteration would race the
    /// test's deterministic <c>RunOnceAsync</c> call — whichever query commits first sweeps the
    /// seeded row. Removing the registration is the only deterministic fix; no other upload test
    /// depends on the cleanup job running.
    /// </summary>
    public sealed class NoSweepFixture : StrgTusUploadFixture
    {
        protected override void ConfigureServicesOverride(IServiceCollection services)
        {
            var cleanupJobRegistration = services.SingleOrDefault(
                d => d.ImplementationType == typeof(AbandonedUploadCleanupJob));
            if (cleanupJobRegistration is not null)
            {
                services.Remove(cleanupJobRegistration);
            }
        }
    }

    private sealed class DeleteFailingProvider(IStorageProvider inner) : IStorageProvider
    {
        public string ProviderType => inner.ProviderType;

        public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
            => throw new IOException($"Simulated DeleteAsync failure for {path}");

        public Task<IStorageFile?> GetFileAsync(string path, CancellationToken cancellationToken = default)
            => inner.GetFileAsync(path, cancellationToken);
        public Task<IStorageDirectory?> GetDirectoryAsync(string path, CancellationToken cancellationToken = default)
            => inner.GetDirectoryAsync(path, cancellationToken);
        public Task<Stream> ReadAsync(string path, long offset = 0, CancellationToken cancellationToken = default)
            => inner.ReadAsync(path, offset, cancellationToken);
        public Task WriteAsync(string path, Stream content, CancellationToken cancellationToken = default)
            => inner.WriteAsync(path, content, cancellationToken);
        public Task AppendAsync(string path, Stream content, CancellationToken cancellationToken = default)
            => inner.AppendAsync(path, content, cancellationToken);
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
