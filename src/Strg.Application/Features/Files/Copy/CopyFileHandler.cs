using MassTransit;
using Mediator;
using Microsoft.Extensions.Logging;
using Strg.Application.Abstractions;
using Strg.Core;
using Strg.Core.Constants;
using Strg.Core.Domain;
using Strg.Core.Events;
using Strg.Core.Services;
using Strg.Plugin.Abstractions.Storage;
using Strg.Plugin.Abstractions.Internal.Encryption;

namespace Strg.Application.Features.Files.Copy;

/// <summary>
/// Copies a <see cref="FileItem"/>'s current head version to a new path, optionally on a different
/// drive. Always relocates bytes through <see cref="IEncryptingFileWriterFactory"/> + raw
/// <see cref="IStorageProvider"/> reads/writes (mirrors
/// <see cref="Move.MoveFileHandler"/>'s cross-drive shape), regardless of whether source and
/// target are the same drive or share encryption posture — the per-file-DEK invariant
/// (<c>project_strg026_encryption_decisions.md</c>) means even a same-drive copy on an encrypted
/// drive needs a fresh DEK so the two FileVersions never share an envelope. No fast-path via
/// <see cref="IStorageProvider.CopyAsync"/> — the parallel branch a fast-path would introduce
/// (plaintext-to-plaintext same-drive only) buys negligible I/O on a narrow case while doubling
/// the failure-cleanup surface.
///
/// <para><b>Quota ordering — Commit-then-write-then-release-on-failure.</b> Per
/// <c>project_strg032_quota_decisions.md</c> single-phase commit-as-reservation:
/// <see cref="IQuotaService.CommitAsync"/> is the atomic UPDATE that reserves budget, called
/// BEFORE the byte copy. If the byte copy or the final SaveChangesAsync throws,
/// <see cref="IQuotaService.ReleaseAsync"/> in a catch block rolls the reservation back. The
/// reservation precedes the storage-write because Check-then-Commit races (a concurrent commit
/// can drain the budget between phases), and a failed Commit short-circuits before any blob hits
/// the target provider.</para>
///
/// <para><b>Outbox publish ordering — opposite of CLAUDE.md.</b>
/// <see cref="IPublishEndpoint.Publish{T}"/> for both <see cref="FileUploadedEvent"/> and
/// <see cref="FileCopiedEvent"/> runs BEFORE <see cref="IStrgDbContext.SaveChangesAsync"/>.
/// MassTransit's <c>UseBusOutbox()</c> interceptor stages each publish on the change tracker as
/// an outbox row; the single subsequent <c>SaveChangesAsync</c> commits the new FileItem +
/// FileVersion + (optional) FileKey + outbox rows in one transaction. CLAUDE.md's "publish AFTER
/// SaveChangesAsync" wording pre-dates the outbox interceptor — see
/// <c>feedback_claudemd_doctrine_vs_wiring.md</c>.</para>
///
/// <para><b>Two events, by design.</b> <see cref="FileUploadedEvent"/> drives audit (via
/// <c>AuditLogConsumer</c> writing the <c>file.uploaded</c> row) and feeds search/notification
/// consumers — the new file IS a new uploaded file from those subsystems' perspective.
/// <see cref="FileCopiedEvent"/> drives the GraphQL subscription stream's
/// <c>FileEventType.Copied</c> discriminator so live UIs render "copied" rather than "uploaded".</para>
///
/// <para><b>Atomicity order.</b> Quota Commit → read source bytes → write target bytes → DB
/// commit (FileItem + FileVersion + (optional) FileKey + outbox) → success. On byte-write
/// failure: best-effort target-bytes cleanup + ReleaseAsync + rethrow. On DB-commit failure:
/// best-effort target-bytes cleanup + ReleaseAsync + rethrow. <b>The original source file and
/// its bytes are never touched</b> — copy is purely additive, distinct from move's source-FileKey
/// removal/replacement and source-bytes deletion.</para>
/// </summary>
internal sealed class CopyFileHandler(
    IStrgDbContext db,
    IFileRepository fileRepository,
    IDriveRepository driveRepository,
    IFileVersionRepository fileVersionRepository,
    IFileKeyRepository fileKeyRepository,
    IStorageProviderRegistry providerRegistry,
    IEncryptingFileWriterFactory encryptingWriterFactory,
    IQuotaService quotaService,
    IPublishEndpoint publishEndpoint,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    ILogger<CopyFileHandler> logger)
    : ICommandHandler<CopyFileCommand, Result<FileItem>>
{
    private const string NotFoundCode = "NotFound";
    private const string InvalidPathCode = "InvalidPath";
    private const string ConflictCode = "Conflict";
    private const string DirectoryCopyUnsupportedCode = "DirectoryCopyUnsupported";

    public async ValueTask<Result<FileItem>> Handle(CopyFileCommand command, CancellationToken cancellationToken)
    {
        StoragePath targetPath;
        try
        {
            targetPath = StoragePath.Parse(command.TargetPath);
        }
        catch (StoragePathException ex)
        {
            return Result<FileItem>.Failure(InvalidPathCode, ex.Message);
        }

        // Resolve source file. Cross-drive id mismatch collapses to NotFound (NOT Forbidden) so
        // the wire shape can't enumerate which drive a file belongs to. Same security stance as
        // MoveFileHandler / DeleteFileHandler.
        var source = await fileRepository.GetByIdAsync(command.FileId, cancellationToken).ConfigureAwait(false);
        if (source is null || source.DriveId != command.DriveId)
        {
            return Result<FileItem>.Failure(NotFoundCode, "File not found.");
        }

        // Reject directory copies in v1.5. Mirrors MoveFileHandler's CrossDriveDirectoryUnsupported
        // — N-blob copy + descendant-row insertion in one DB transaction is deferred. Acceptance
        // criteria explicitly mention "files" (singular); STRG-040's deferral pattern applies here
        // for ALL directory copies (within-drive included), because a within-drive directory copy
        // still requires N fresh storage keys + N FileVersion rows — unlike within-drive directory
        // MOVE which is a pure DB-rewrite of paths.
        if (source.IsDirectory)
        {
            return Result<FileItem>.Failure(
                DirectoryCopyUnsupportedCode,
                "Directory copy is not supported in v1.5. See follow-up issue.");
        }

        var targetDriveId = command.TargetDriveId ?? command.DriveId;

        // Resolve target drive. Cross-tenant or soft-deleted target drive collapses to NotFound
        // via the global query filter — the wire response cannot distinguish missing-drive from
        // wrong-tenant.
        var targetDrive = await driveRepository.GetByIdAsync(targetDriveId, cancellationToken).ConfigureAwait(false);
        if (targetDrive is null)
        {
            return Result<FileItem>.Failure(NotFoundCode, "Target drive not found.");
        }

        // Resolve source drive — needed for provider read in every encryption combo.
        var sourceDrive = await driveRepository.GetByIdAsync(source.DriveId, cancellationToken).ConfigureAwait(false);
        if (sourceDrive is null)
        {
            // Practically unreachable — the file was loaded above. Defensive against same-tx
            // soft-delete races on the drive.
            return Result<FileItem>.Failure(NotFoundCode, "Source drive not found.");
        }

        // Collision check on (target drive, target path). Catches the same-source-self-collision
        // case naturally — when targetDriveId == driveId && targetPath == source.Path, the source
        // row IS the collision (and copy must produce a NEW row at a NEW path). Soft-deleted rows
        // are excluded by the global query filter, so a previously-deleted target path is reusable.
        var collision = await fileRepository
            .GetByPathAsync(targetDriveId, targetPath.Value, cancellationToken)
            .ConfigureAwait(false);
        if (collision is not null)
        {
            return Result<FileItem>.Failure(ConflictCode, "Target path already exists.");
        }

        // Load the head FileVersion for the source. file.VersionCount points at the head — same
        // convention FileDownloadResolver / MoveFileHandler use. FileVersion is NOT tenanted;
        // reaching it via the tenant-filtered file is the only safe path.
        if (source.VersionCount <= 0)
        {
            return Result<FileItem>.Failure(NotFoundCode, "Source file has no readable version.");
        }
        var sourceVersion = await fileVersionRepository
            .GetAsync(source.Id, source.VersionCount, cancellationToken)
            .ConfigureAwait(false);
        if (sourceVersion is null)
        {
            return Result<FileItem>.Failure(NotFoundCode, "Source version row missing.");
        }

        var sourceProvider = providerRegistry.Resolve(
            sourceDrive.ProviderType,
            DictionaryStorageProviderConfig.FromJson(sourceDrive.ProviderConfig));
        var targetProvider = providerRegistry.Resolve(
            targetDrive.ProviderType,
            DictionaryStorageProviderConfig.FromJson(targetDrive.ProviderConfig));

        // Reserve quota for the copy. Plaintext-denominated per STRG-026 #5; source.Size is the
        // pre-encryption length and is what counts against the user's quota. Throws
        // QuotaExceededException on shortfall — endpoint catches and maps to 507. Caller is the
        // current authenticated user (cross-user attribution is out of scope; no sharing in v1).
        await quotaService.CommitAsync(currentUser.UserId, source.Size, cancellationToken).ConfigureAwait(false);

        // Compute the new file id and target storage key NOW so the cleanup branch in the catch
        // can address them by value without re-deriving inside the dispose path.
        var newFileId = Guid.NewGuid();
        const int newVersionNumber = 1;
        var targetKey = StrgUploadKeys.FinalKey(targetDriveId, newFileId, newVersionNumber);

        // Read plaintext stream (decrypt if source is encrypted), write to target with fresh key
        // (encrypt with fresh DEK if target is encrypted). On any throw between read and write,
        // best-effort delete the freshly-written target so we don't strand an unreachable blob,
        // and release the quota reservation.
        FileKey? sourceFileKey = null;
        Stream? plaintextStream = null;
        byte[]? targetWrappedDek = null;
        string? targetAlgorithm = null;
        long targetBlobSize;
        try
        {
            if (sourceDrive.EncryptionEnabled)
            {
                sourceFileKey = await fileKeyRepository
                    .GetByFileVersionAsync(sourceVersion.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (sourceFileKey is null)
                {
                    // Quota was reserved above — release before we abandon the workflow.
                    await SafeReleaseAsync(source.Size, CancellationToken.None).ConfigureAwait(false);
                    return Result<FileItem>.Failure(NotFoundCode, "Source FileKey missing for encrypted drive.");
                }
                plaintextStream = await encryptingWriterFactory
                    .Create(sourceProvider)
                    .ReadAsync(sourceVersion.StorageKey, sourceFileKey.EncryptedDek, sourceFileKey.Algorithm, 0, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                plaintextStream = await sourceProvider
                    .ReadAsync(sourceVersion.StorageKey, 0, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (targetDrive.EncryptionEnabled)
            {
                var writeResult = await encryptingWriterFactory
                    .Create(targetProvider)
                    .WriteAsync(targetKey, plaintextStream, EncryptionAlgorithms.AesGcm256, cancellationToken)
                    .ConfigureAwait(false);
                targetWrappedDek = writeResult.WrappedDek;
                targetAlgorithm = writeResult.Algorithm;

                var meta = await targetProvider.GetFileAsync(targetKey, cancellationToken).ConfigureAwait(false);
                targetBlobSize = meta?.Size ?? sourceVersion.BlobSizeBytes;
            }
            else
            {
                await targetProvider.WriteAsync(targetKey, plaintextStream, cancellationToken).ConfigureAwait(false);
                targetBlobSize = source.Size;
            }
        }
        catch
        {
            // CancellationToken.None so caller cancellation doesn't strand the reservation as
            // well — this branch is already handling a primary failure, the cleanup is a courtesy.
            await SafeReleaseAsync(source.Size, CancellationToken.None).ConfigureAwait(false);
            try
            {
                await targetProvider.DeleteAsync(targetKey, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupEx)
            {
                logger.LogWarning(cleanupEx,
                    "Copy {SourceFileId} → {NewFileId}: target cleanup after write failure failed; orphan at {TargetKey}.",
                    source.Id, newFileId, targetKey);
            }
            throw;
        }
        finally
        {
            if (plaintextStream is not null)
            {
                await plaintextStream.DisposeAsync().ConfigureAwait(false);
            }
        }

        // Stage DB rows. New FileItem with a fresh Id, new FileVersion at version 1, new FileKey
        // only if the target drive is encrypted. The source FileKey row is NEVER touched —
        // distinct from MoveFileHandler's E→P / E→E branches which Remove or Replace it.
        var newFile = new FileItem
        {
            Id = newFileId,
            TenantId = tenantContext.TenantId,
            DriveId = targetDriveId,
            Name = ExtractNameFromPath(targetPath.Value),
            Path = targetPath.Value,
            Size = source.Size,
            ContentHash = sourceVersion.ContentHash,
            IsDirectory = false,
            CreatedBy = currentUser.UserId,
            MimeType = source.MimeType,
            VersionCount = newVersionNumber,
            StorageKey = targetKey,
        };
        await fileRepository.AddAsync(newFile, cancellationToken).ConfigureAwait(false);

        var newVersion = new FileVersion
        {
            FileId = newFileId,
            VersionNumber = newVersionNumber,
            Size = source.Size,
            BlobSizeBytes = targetBlobSize,
            ContentHash = sourceVersion.ContentHash,
            StorageKey = targetKey,
            CreatedBy = currentUser.UserId,
        };
        await fileVersionRepository.AddAsync(newVersion, cancellationToken).ConfigureAwait(false);

        if (targetDrive.EncryptionEnabled)
        {
            await fileKeyRepository.AddAsync(new FileKey
            {
                FileVersionId = newVersion.Id,
                EncryptedDek = targetWrappedDek!,
                Algorithm = targetAlgorithm!,
            }, cancellationToken).ConfigureAwait(false);
        }

        // Publish FileUploadedEvent (drives audit + downstream consumers) AND FileCopiedEvent
        // (drives GraphQL subscription discriminator). Both BEFORE SaveChangesAsync per the
        // outbox-interceptor contract.
        await publishEndpoint.Publish(
            new FileUploadedEvent(
                tenantContext.TenantId,
                newFileId,
                targetDriveId,
                currentUser.UserId,
                source.Size,
                source.MimeType),
            cancellationToken).ConfigureAwait(false);

        await publishEndpoint.Publish(
            new FileCopiedEvent(
                newFileId,
                targetDriveId,
                currentUser.UserId,
                tenantContext.TenantId,
                targetPath.Value),
            cancellationToken).ConfigureAwait(false);

        // Atomic commit. On failure: release quota + clean up target bytes + rethrow.
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await SafeReleaseAsync(source.Size, CancellationToken.None).ConfigureAwait(false);
            try
            {
                await targetProvider.DeleteAsync(targetKey, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupEx)
            {
                logger.LogWarning(cleanupEx,
                    "Copy {SourceFileId} → {NewFileId}: target cleanup after DB commit failure failed; orphan at {TargetKey}.",
                    source.Id, newFileId, targetKey);
            }
            throw;
        }

        return Result<FileItem>.Success(newFile);
    }

    private async Task SafeReleaseAsync(long bytes, CancellationToken cancellationToken)
    {
        try
        {
            await quotaService.ReleaseAsync(currentUser.UserId, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Copy compensation: failed to release {Bytes} bytes from user {UserId}; quota may show transient overshoot.",
                bytes, currentUser.UserId);
        }
    }

    private static string ExtractNameFromPath(string normalizedPath) =>
        normalizedPath.Split('/').Last(s => s.Length > 0);
}
