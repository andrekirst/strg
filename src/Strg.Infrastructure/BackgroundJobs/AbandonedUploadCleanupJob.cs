using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Strg.Core.Domain;
using Strg.Core.Storage;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Upload;

namespace Strg.Infrastructure.BackgroundJobs;

/// <summary>
/// Periodic sweep that reaps abandoned in-flight TUS uploads (STRG-035). Walks
/// <see cref="PendingUpload"/> rows past their <see cref="PendingUpload.ExpiresAt"/> deadline
/// that have not yet flipped <see cref="PendingUpload.IsCompleted"/> true, deletes both the temp
/// blob (<see cref="PendingUpload.TempStorageKey"/>) and its <c>.part</c> sidecar from storage,
/// and removes the DB row.
///
/// <para><b>Why no quota release.</b> STRG-035's spec text says the job should release reserved
/// quota on each abandoned upload. STRG-034 shipped quota with a "Commit IS the reservation"
/// model — see <see cref="Core.Services.IQuotaService"/> class summary; the only call to
/// <see cref="Core.Services.IQuotaService.CommitAsync"/> on the upload path is at
/// <see cref="Strg.Infrastructure.Upload.StrgTusStore.FinalizeAsync"/> after IsCompleted flips.
/// Therefore <c>IsCompleted = false</c> rows have committed exactly zero bytes and there is
/// nothing to release. Calling <see cref="Core.Services.IQuotaService.ReleaseAsync"/> with
/// <see cref="PendingUpload.DeclaredSize"/> would silently under-charge the user against their
/// real committed usage. This job is therefore a pure row + temp-blob reaper for
/// <c>IsCompleted = false</c> state.</para>
///
/// <para><b>Phase-3 inverted rows are out of scope.</b> When
/// <see cref="Strg.Infrastructure.Upload.StrgTusStore.FinalizeAsync"/> commits the DB transaction
/// but the post-commit MoveAsync fails, the row stays with <c>IsCompleted = true</c> and the
/// committed quota is real but the final blob is missing. Recovering that state is a separate
/// concern (different storage operations, real quota release semantics) and is deliberately not
/// folded into this job — the query predicate is <c>!p.IsCompleted</c>.</para>
///
/// <para><b>Cross-tenant carve-out.</b> The job runs without an HttpContext, so the default
/// <see cref="HttpTenantContext"/> returns <see cref="Guid.Empty"/> and the global tenant filter
/// would scope every query to <c>TenantId = Guid.Empty</c>, hiding all real-tenant rows. We
/// disable ONLY the tenant filter via <c>IgnoreQueryFilters([StrgDbContext.TenantFilterName])</c>;
/// the soft-delete filter stays active so soft-deleted rows remain excluded. The carve-out is
/// security-safe because the job is a sink (deletes rows) — it never returns data to a caller —
/// and there is no tenant boundary to leak across because there is no caller. Mirrors the precedent
/// at <see cref="Identity.FirstRunInitializationService"/> for cross-tenant startup work.</para>
///
/// <para><b>Composite-index note.</b> The shipped index
/// <c>IX_PendingUploads_TenantId_IsCompleted_ExpiresAt</c> is suboptimal once the leading
/// <c>TenantId</c> column drops out of the WHERE clause — at scale, a partial index keyed on
/// <c>(IsCompleted, ExpiresAt)</c> would be tighter. v0.1 row counts make the cost negligible;
/// see follow-up issue filed at STRG-035 close.</para>
///
/// <para><b>ParseProviderConfig duplication.</b> The provider-config JSON parser is duplicated
/// here from <see cref="Strg.Infrastructure.Upload.StrgTusStore"/> /
/// <c>StrgWebDavStore.ParseProviderConfig</c> /
/// <see cref="Strg.Infrastructure.HealthChecks.StorageHealthCheck"/>. The HealthCheck's comment
/// documents the explicit "kept inline" convention; this is the fourth call site. A shared helper
/// is the obvious refactor and is left for a future change.</para>
/// </summary>
public sealed class AbandonedUploadCleanupJob(
    IServiceScopeFactory scopeFactory,
    IStorageProviderRegistry providerRegistry,
    TimeProvider timeProvider,
    IOptions<StrgTusOptions> options,
    ILogger<AbandonedUploadCleanupJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var swept = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                if (swept > 0)
                {
                    logger.LogInformation(
                        "AbandonedUploadCleanupJob swept {Count} abandoned upload(s)", swept);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never propagate: an unhandled throw out of ExecuteAsync stops the host
                // (BackgroundServiceExceptionBehavior.StopHost is the .NET default).
                logger.LogError(ex, "AbandonedUploadCleanupJob sweep iteration failed");
            }

            try
            {
                await Task.Delay(options.Value.UploadCleanupInterval, timeProvider, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Runs a single sweep pass and returns the number of <see cref="PendingUpload"/> rows
    /// removed. Exposed as <c>internal</c> so integration tests can drive the body deterministically
    /// without spinning the host timer (see <c>InternalsVisibleTo("Strg.Integration.Tests")</c> in
    /// the project file).
    /// </summary>
    internal async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var now = timeProvider.GetUtcNow();

        var expired = await db.PendingUploads
            .IgnoreQueryFilters([StrgDbContext.TenantFilterName])
            .Where(p => !p.IsCompleted && p.ExpiresAt < now)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (expired.Count == 0)
        {
            return 0;
        }

        foreach (var upload in expired)
        {
            await TryDeleteBlobAsync(db, upload, cancellationToken).ConfigureAwait(false);
            db.PendingUploads.Remove(upload);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return expired.Count;
    }

    private async Task TryDeleteBlobAsync(
        StrgDbContext db, PendingUpload upload, CancellationToken cancellationToken)
    {
        var drive = await db.Drives
            .IgnoreQueryFilters([StrgDbContext.TenantFilterName])
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == upload.DriveId, cancellationToken)
            .ConfigureAwait(false);

        if (drive is null)
        {
            // Drive deleted (hard or soft) before the sweep ran. The temp blob — if it ever made
            // it to disk — is either gone with the drive's namespace or stranded outside the
            // job's authority. Continue to remove the orphan PendingUpload row.
            logger.LogWarning(
                "AbandonedUploadCleanupJob: PendingUpload {UploadId} references drive {DriveId} which is not visible (deleted or soft-deleted); skipping storage delete",
                upload.UploadId, upload.DriveId);
            return;
        }

        IStorageProvider provider;
        try
        {
            var config = ParseProviderConfig(drive.ProviderConfig);
            provider = providerRegistry.Resolve(drive.ProviderType, config);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "AbandonedUploadCleanupJob: failed to resolve provider for upload {UploadId} (drive {DriveId}, providerType {ProviderType}); skipping storage delete",
                upload.UploadId, upload.DriveId, drive.ProviderType);
            return;
        }

        await BestEffortDeleteAsync(provider, upload.TempStorageKey, upload.UploadId, cancellationToken)
            .ConfigureAwait(false);
        await BestEffortDeleteAsync(provider, upload.TempStorageKey + ".part", upload.UploadId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task BestEffortDeleteAsync(
        IStorageProvider provider, string key, Guid uploadId, CancellationToken cancellationToken)
    {
        try
        {
            await provider.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "AbandonedUploadCleanupJob: best-effort DeleteAsync failed for upload {UploadId} at {StorageKey}; row will still be removed",
                uploadId, key);
        }
    }

    private static IStorageProviderConfig ParseProviderConfig(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
        {
            return new DictionaryStorageProviderConfig(new Dictionary<string, string?>());
        }
        var raw = JsonSerializer.Deserialize<Dictionary<string, string?>>(json)
            ?? new Dictionary<string, string?>();
        return new DictionaryStorageProviderConfig(raw);
    }
}
