using System.Text.Json;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Strg.Core.Constants;
using Strg.Core.Domain;
using Strg.Core.Events;
using Strg.Core.Storage;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Storage.Encryption;
using tusdotnet.Interfaces;
using tusdotnet.Models;

namespace Strg.Infrastructure.Upload;

/// <summary>
/// TUS protocol store backed by <see cref="IStorageProvider"/> + <see cref="IEncryptingFileWriter"/>
/// with a two-phase finalize that closes the orphan-ciphertext gap (STRG-026 #2 / STRG-034 spec).
///
/// <para><b>DbContext lifetime.</b> Each store method creates its OWN <see cref="StrgDbContext"/>
/// from the injected <see cref="DbContextOptions{TContext}"/> rather than sharing the per-request
/// scoped context. tusdotnet's validation pipeline can interleave calls to multiple read methods
/// (e.g., <see cref="FileExistAsync"/> and <see cref="GetUploadOffsetAsync"/>) during a single
/// PATCH; sharing one DbContext across them produces "A second operation was started on this
/// context instance" exceptions when the queries overlap. A per-method DbContext is
/// concurrency-safe and the cost is minor — each method does at most one round-trip.</para>
///
/// <para><b>Lifecycle.</b></para>
/// <list type="number">
///   <item><c>POST /upload?driveId=…</c> with <c>Upload-Length</c> + <c>Upload-Metadata</c>:
///     <c>OnBeforeCreateAsync</c> (in <see cref="StrgTusEvents"/>) validates auth, parses metadata,
///     runs <see cref="StoragePath.Parse"/>, runs the pre-quota check, and stashes the validated
///     parts into <c>HttpContext.Items</c>. <see cref="CreateFileAsync"/> reads those, generates
///     an upload id, and stages a <see cref="PendingUpload"/> row.</item>
///   <item><c>PATCH /upload/{id}</c>: <see cref="AppendDataAsync"/> appends RAW plaintext bytes to
///     <c>{TempStorageKey}.part</c> via <see cref="IStorageProvider.AppendAsync"/>.</item>
///   <item>Last PATCH: tusdotnet fires <c>OnFileCompleteAsync</c> which calls
///     <see cref="FinalizeAsync"/> — encrypts the assembled raw blob, runs the atomic DB commit,
///     promotes the temp key to the final key.</item>
/// </list>
/// </summary>
public sealed class StrgTusStore(
    DbContextOptions<StrgDbContext> dbOptions,
    IStorageProviderRegistry providerRegistry,
    IKeyProvider keyProvider,
    IPublishEndpoint publishEndpoint,
    ITenantContext tenantContext,
    IHttpContextAccessor httpContextAccessor,
    TimeProvider timeProvider,
    IOptions<StrgTusOptions> options,
    ILogger<StrgTusStore> logger)
    : ITusStore, ITusCreationStore, ITusReadableStore, ITusTerminationStore
{
    internal const string ItemKeyDriveId = "Strg.Tus.DriveId";
    internal const string ItemKeyPath = "Strg.Tus.Path";
    internal const string ItemKeyFilename = "Strg.Tus.Filename";
    internal const string ItemKeyMimeType = "Strg.Tus.MimeType";

    private StrgDbContext NewDb() => new(dbOptions, tenantContext);

    // ── ITusStore ─────────────────────────────────────────────────────────────

    public async Task<bool> FileExistAsync(string fileId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(fileId, "N", out var uploadId))
        {
            return false;
        }
        await using var db = NewDb();
        return await db.PendingUploads.AsNoTracking()
            .AnyAsync(p => p.UploadId == uploadId && !p.IsCompleted, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<long?> GetUploadLengthAsync(string fileId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(fileId, "N", out var uploadId))
        {
            return null;
        }
        await using var db = NewDb();
        var declaredSize = await db.PendingUploads.AsNoTracking()
            .Where(p => p.UploadId == uploadId && !p.IsCompleted)
            .Select(p => (long?)p.DeclaredSize)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return declaredSize;
    }

    public async Task<long> GetUploadOffsetAsync(string fileId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(fileId, "N", out var uploadId))
        {
            throw new TusStoreException($"Upload {fileId} not found");
        }
        await using var db = NewDb();
        var offset = await db.PendingUploads.AsNoTracking()
            .Where(p => p.UploadId == uploadId && !p.IsCompleted)
            .Select(p => (long?)p.UploadOffset)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (offset is null)
        {
            throw new TusStoreException($"Upload {fileId} not found");
        }
        return offset.Value;
    }

    public async Task<long> AppendDataAsync(string fileId, Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!Guid.TryParseExact(fileId, "N", out var uploadId))
        {
            throw new TusStoreException($"Upload {fileId} not found");
        }

        await using var db = NewDb();
        var pending = await db.PendingUploads
            .FirstOrDefaultAsync(p => p.UploadId == uploadId && !p.IsCompleted, cancellationToken)
            .ConfigureAwait(false);
        if (pending is null)
        {
            throw new TusStoreException($"Upload {fileId} not found");
        }

        var provider = await ResolveProviderAsync(db, pending.DriveId, cancellationToken).ConfigureAwait(false);
        var partKey = pending.TempStorageKey + ".part";

        // Wrap to count bytes — IStorageProvider.AppendAsync returns void and the request body
        // doesn't always report Length on chunked-transfer-encoded streams.
        var counting = new CountingReadStream(stream);
        await provider.AppendAsync(partKey, counting, cancellationToken).ConfigureAwait(false);

        pending.UploadOffset += counting.BytesRead;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return counting.BytesRead;
    }

    // ── ITusCreationStore ─────────────────────────────────────────────────────

    public async Task<string> CreateFileAsync(long uploadLength, string metadata, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("StrgTusStore.CreateFileAsync called without an HttpContext");

        var driveId = (Guid)httpContext.Items[ItemKeyDriveId]!;
        var path = (string)httpContext.Items[ItemKeyPath]!;
        var filename = (string)httpContext.Items[ItemKeyFilename]!;
        var mimeType = (string)httpContext.Items[ItemKeyMimeType]!;

        var uploadId = Guid.NewGuid();
        var tempKey = StrgUploadKeys.TempKey(driveId, uploadId);

        var pending = new PendingUpload
        {
            TenantId = tenantContext.TenantId,
            UploadId = uploadId,
            DriveId = driveId,
            UserId = ResolveCurrentUserId(httpContext),
            Path = path,
            Filename = filename,
            MimeType = mimeType,
            DeclaredSize = uploadLength,
            ExpiresAt = timeProvider.GetUtcNow() + options.Value.UploadAbandonAfter,
            TempStorageKey = tempKey,
        };

        await using var db = NewDb();
        db.PendingUploads.Add(pending);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return uploadId.ToString("N");
    }

    public async Task<string> GetUploadMetadataAsync(string fileId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(fileId, "N", out var uploadId))
        {
            throw new TusStoreException($"Upload {fileId} not found");
        }
        await using var db = NewDb();
        var pending = await db.PendingUploads.AsNoTracking()
            .Where(p => p.UploadId == uploadId)
            .Select(p => new { p.Path, p.Filename, p.MimeType })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (pending is null)
        {
            throw new TusStoreException($"Upload {fileId} not found");
        }
        return BuildMetadataHeader(pending.Path, pending.Filename, pending.MimeType);
    }

    // ── ITusReadableStore ─────────────────────────────────────────────────────

    public Task<ITusFile?> GetFileAsync(string fileId, CancellationToken cancellationToken)
    {
        // STRG-034 does not expose downloads through the TUS surface — the production read path
        // is the dedicated download endpoint that uses FileVersion.StorageKey + FileKey.Algorithm.
        return Task.FromResult<ITusFile?>(null);
    }

    // ── ITusTerminationStore ─────────────────────────────────────────────────

    public async Task DeleteFileAsync(string fileId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(fileId, "N", out var uploadId))
        {
            throw new TusStoreException($"Upload {fileId} not found");
        }

        await using var db = NewDb();
        var pending = await db.PendingUploads
            .FirstOrDefaultAsync(p => p.UploadId == uploadId, cancellationToken)
            .ConfigureAwait(false);
        if (pending is null)
        {
            throw new TusStoreException($"Upload {fileId} not found");
        }
        if (pending.IsCompleted)
        {
            throw new TusStoreException($"Upload {fileId} has already started finalizing");
        }

        var provider = await ResolveProviderAsync(db, pending.DriveId, cancellationToken).ConfigureAwait(false);

        // Best-effort cleanup of both the encrypted target and the .part sidecar.
        await provider.DeleteAsync(pending.TempStorageKey, cancellationToken).ConfigureAwait(false);
        await provider.DeleteAsync(pending.TempStorageKey + ".part", cancellationToken).ConfigureAwait(false);

        db.PendingUploads.Remove(pending);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // ── Custom: invoked from OnFileCompleteAsync ─────────────────────────────

    /// <summary>
    /// Closes the upload: encrypts the assembled raw blob, runs the atomic DB commit
    /// (FileItem + FileVersion + FileKey + quota + outbox publish), and promotes the temp key
    /// to the final key. See class summary for the strict ordering this method enforces.
    /// </summary>
    public async Task FinalizeAsync(string fileId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(fileId, "N", out var uploadId))
        {
            throw new TusStoreException($"Upload {fileId} not found");
        }

        // Open ONE DbContext for the entire finalize so the transaction + entity tracking work.
        await using var db = NewDb();

        var pending = await db.PendingUploads
            .FirstOrDefaultAsync(p => p.UploadId == uploadId, cancellationToken)
            .ConfigureAwait(false);
        if (pending is null)
        {
            throw new TusStoreException($"Upload {fileId} not found");
        }
        if (pending.IsCompleted)
        {
            // Re-fire of OnFileCompleteAsync must NOT run finalize twice.
            logger.LogWarning("FinalizeAsync for upload {UploadId} skipped: already completed", pending.UploadId);
            return;
        }

        var provider = await ResolveProviderAsync(db, pending.DriveId, cancellationToken).ConfigureAwait(false);
        var partKey = pending.TempStorageKey + ".part";

        // ── Phase 1: encrypt the assembled raw blob ────────────────────────
        var encryptingWriter = new AesGcmFileWriter(provider, keyProvider);
        EncryptedWriteResult encryptedResult;
        string contentHash;
        long blobSize;
        try
        {
            await using var rawStream = await provider.ReadAsync(partKey, offset: 0, cancellationToken)
                .ConfigureAwait(false);
            await using var hashingStream = new Sha256ReadStream(rawStream);

            encryptedResult = await encryptingWriter.WriteAsync(
                pending.TempStorageKey,
                hashingStream,
                AesGcmFileWriter.AlgorithmName,
                cancellationToken).ConfigureAwait(false);

            contentHash = hashingStream.GetHashHex();

            var encryptedFile = await provider.GetFileAsync(pending.TempStorageKey, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Encrypted blob missing immediately after write at {pending.TempStorageKey}");
            blobSize = encryptedFile.Size;
        }
        catch
        {
            await TryDeleteAsync(provider, pending.TempStorageKey).ConfigureAwait(false);
            await TryDeleteAsync(provider, partKey).ConfigureAwait(false);
            throw;
        }

        // ── Phase 2: atomic DB commit ───────────────────────────────────────
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        Guid fileItemId;
        string finalKey;
        try
        {
            var file = new FileItem
            {
                TenantId = tenantContext.TenantId,
                DriveId = pending.DriveId,
                Name = pending.Filename,
                Path = pending.Path,
                Size = encryptedResult.Length,
                ContentHash = contentHash,
                IsDirectory = false,
                CreatedBy = pending.UserId,
                MimeType = pending.MimeType,
                VersionCount = 1,
            };
            db.Files.Add(file);

            finalKey = StrgUploadKeys.FinalKey(pending.DriveId, file.Id, versionNumber: 1);
            file.StorageKey = finalKey;

            var version = new FileVersion
            {
                FileId = file.Id,
                VersionNumber = 1,
                Size = encryptedResult.Length,
                BlobSizeBytes = blobSize,
                ContentHash = contentHash,
                StorageKey = finalKey,
                CreatedBy = pending.UserId,
            };
            db.FileVersions.Add(version);

            var fileKeyRow = new FileKey
            {
                FileVersionId = version.Id,
                EncryptedDek = encryptedResult.WrappedDek,
                Algorithm = encryptedResult.Algorithm,
            };
            db.FileKeys.Add(fileKeyRow);

            // Quota commit — STRG-032 plaintext-denominated. Build a transient QuotaService bound
            // to OUR DbContext (not the request-scoped one) so the atomic UPDATE participates in
            // the same ambient transaction.
            var quotaService = new Strg.Infrastructure.Services.QuotaService(
                db, tenantContext, publishEndpoint, NullLogger<Strg.Infrastructure.Services.QuotaService>.Instance);
            await quotaService.CommitAsync(pending.UserId, encryptedResult.Length, cancellationToken)
                .ConfigureAwait(false);

            // Outbox publish + SaveChangesAsync flushes outbox row in same tx. Per project memory's
            // "doctrine vs wiring" note, Publish-then-SaveChangesAsync is the order that works
            // under MassTransit's interceptor (CLAUDE.md's "publish AFTER SaveChangesAsync" wording
            // refers to atomicity, not call order).
            await publishEndpoint.Publish(
                new FileUploadedEvent(
                    tenantContext.TenantId,
                    file.Id,
                    pending.DriveId,
                    pending.UserId,
                    encryptedResult.Length,
                    pending.MimeType),
                cancellationToken).ConfigureAwait(false);

            // Mark IsCompleted BEFORE MoveAsync so a phase-3 inversion leaves the row in a state
            // STRG-036's sweep can recognise: IsCompleted=true means "DB is committed, MoveAsync
            // may or may not have run".
            pending.IsCompleted = true;
            pending.WrappedDek = encryptedResult.WrappedDek;
            pending.Algorithm = encryptedResult.Algorithm;
            pending.ContentHash = contentHash;
            pending.PlaintextSize = encryptedResult.Length;
            pending.BlobSizeBytes = blobSize;

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            fileItemId = file.Id;
        }
        catch
        {
            await TryDeleteAsync(provider, pending.TempStorageKey).ConfigureAwait(false);
            await TryDeleteAsync(provider, partKey).ConfigureAwait(false);
            throw;
        }

        // ── Phase 3: promote temp → final (post-commit, NO rollback after this) ───
        try
        {
            await provider.MoveAsync(pending.TempStorageKey, finalKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception moveEx)
        {
            // Phase-3 inversion. DB committed; bytes still at TempStorageKey. STRG-036's backstop
            // sweep is the recovery path. Re-throw so the TUS client sees the upload as failed.
            logger.LogWarning(moveEx,
                "Phase-3 inversion for upload {UploadId}: DB committed but temp→final promote failed at {TempKey} → {FinalKey}. STRG-036's sweep is the recovery path.",
                pending.UploadId, pending.TempStorageKey, finalKey);
            throw;
        }

        // Best-effort cleanup. Failures here are reaped by STRG-036.
        await TryDeleteAsync(provider, partKey).ConfigureAwait(false);
        try
        {
            await using var cleanupDb = NewDb();
            await cleanupDb.PendingUploads
                .Where(p => p.UploadId == uploadId)
                .ExecuteDeleteAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Best-effort PendingUpload row removal failed for {UploadId}; STRG-036's sweep is the backstop.",
                pending.UploadId);
        }

        _ = fileItemId; // silence unused warning — kept for future telemetry hookup
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<IStorageProvider> ResolveProviderAsync(
        StrgDbContext db, Guid driveId, CancellationToken cancellationToken)
    {
        var drive = await db.Drives.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == driveId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Drive {driveId} not found");
        var config = ParseProviderConfig(drive.ProviderConfig);
        return providerRegistry.Resolve(drive.ProviderType, config);
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

    private async Task TryDeleteAsync(IStorageProvider provider, string key)
    {
        try
        {
            await provider.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Best-effort DeleteAsync failed for {StorageKey}", key);
        }
    }

    private static Guid ResolveCurrentUserId(HttpContext httpContext)
    {
        var claim = httpContext.User.FindFirst(StrgClaimNames.Subject)?.Value;
        return Guid.TryParse(claim, out var id) ? id : Guid.Empty;
    }

    private static string BuildMetadataHeader(string path, string filename, string mimeType)
    {
        return string.Join(",",
            $"path {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(path))}",
            $"filename {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(filename))}",
            $"contentType {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(mimeType))}");
    }
}
