using MassTransit;
using Mediator;
using Microsoft.Extensions.Logging;
using Strg.Application.Abstractions;
using Strg.Core;
using Strg.Core.Constants;
using Strg.Core.Domain;
using Strg.Core.Events;
using Strg.Plugin.Abstractions.Storage;
using Strg.Plugin.Abstractions.Internal.Encryption;

namespace Strg.Application.Features.Files.Move;

/// <summary>
/// Moves a <see cref="FileItem"/> to a new path, optionally across drives, and publishes
/// <see cref="FileMovedEvent"/> via the MassTransit outbox so <c>AuditLogConsumer</c> writes the
/// audit row asynchronously.
///
/// <para><b>Phase 2 scope (STRG-040 v1.5).</b> Phase 1 supported within-drive single-file moves
/// only. Phase 2 enables three additional shapes:
/// <list type="bullet">
///   <item><description><b>Within-drive directory move</b> — root row's path mutates and every
///   descendant's path is rewritten under the new prefix in the same DB transaction. The bytes
///   never relocate (storage keys are anchored on <c>FileItem.Id</c>, not <c>Path</c>).</description></item>
///   <item><description><b>Cross-drive single-file move</b> — bytes are copied to the target drive's
///   provider with a fresh storage key, the FileVersion row is rebased onto the new key/blob-size,
///   and the FileKey row lifecycle adapts to the target's encryption posture (Add/Remove/Replace).
///   Source bytes are deleted best-effort after DB commit.</description></item>
///   <item><description><b>Cross-drive directory move</b> — REJECTED in v1.5 with the dedicated
///   <see cref="CrossDriveDirectoryUnsupportedCode"/> error code. Defers the combinatorial
///   complexity of N-blob copy + descendant path rewrite to a follow-up tracker.</description></item>
/// </list></para>
///
/// <para><b>Atomicity order on cross-drive.</b> Read source → write target → DB commit
/// (FileItem mutation + FileVersion rebase + FileKey lifecycle + outbox publish) → best-effort
/// source-bytes delete. Write failure compensates with target-bytes cleanup; DB-commit failure
/// abandons the target bytes (no reaper covers <c>drives/</c> prefix today — captured as a
/// follow-up). Source-bytes delete failure logs a warning and leaves an orphan; downstream reads
/// already collapse to the new <c>StorageKey</c> via the rebased FileVersion, so user-visible
/// behaviour is correct regardless.</para>
///
/// <para><b>Outbox publish ordering — opposite of CLAUDE.md.</b> <see cref="IPublishEndpoint.Publish{T}"/>
/// runs BEFORE <see cref="IStrgDbContext.SaveChangesAsync"/>. MassTransit's <c>UseBusOutbox()</c>
/// interceptor stages the publish on the change tracker as an outbox row; the single subsequent
/// <c>SaveChangesAsync</c> commits the file mutation and the outbox row in one transaction.
/// CLAUDE.md's "publish AFTER SaveChangesAsync" doctrine pre-dates the <c>UseBusOutbox()</c>
/// wiring — see <c>DeleteFileHandler</c> and <c>StrgTusStore</c> for the canonical
/// publish-before-save pattern.</para>
///
/// <para><b>One event for tree mutation.</b> Directory moves emit ONE <see cref="FileMovedEvent"/>
/// for the root, mirroring <c>DeleteFileHandler</c>'s soft-delete behaviour. Per-descendant events
/// would create an N-blob audit storm for a single user action.</para>
/// </summary>
internal sealed class MoveFileHandler(
    IStrgDbContext db,
    IFileRepository fileRepository,
    IDriveRepository driveRepository,
    IFileVersionRepository fileVersionRepository,
    IFileKeyRepository fileKeyRepository,
    IStorageProviderRegistry providerRegistry,
    IEncryptingFileWriterFactory encryptingWriterFactory,
    IPublishEndpoint publishEndpoint,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    ILogger<MoveFileHandler> logger)
    : ICommandHandler<MoveFileCommand, Result<FileItem>>
{
    private const string NotFoundCode = "NotFound";
    private const string InvalidPathCode = "InvalidPath";
    private const string ConflictCode = "Conflict";
    private const string CrossDriveDirectoryUnsupportedCode = "CrossDriveDirectoryUnsupported";

    public async ValueTask<Result<FileItem>> Handle(MoveFileCommand command, CancellationToken cancellationToken)
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

        var file = await fileRepository.GetByIdAsync(command.FileId, cancellationToken).ConfigureAwait(false);
        if (file is null || file.DriveId != command.DriveId)
        {
            // Cross-drive id mismatch is collapsed to NotFound (NOT Forbidden) so the wire shape
            // cannot enumerate which drive a file belongs to. Same security stance as
            // FileDownloadResolver and DeleteFileHandler.
            return Result<FileItem>.Failure(NotFoundCode, "File not found.");
        }

        var targetDriveId = command.TargetDriveId ?? command.DriveId;
        var isCrossDrive = targetDriveId != command.DriveId;

        // v1.5 rejects cross-drive directory moves. Per-descendant blob copy + descendant path
        // rewrite is doable but the combinatorial blast radius (N storage round-trips inside one
        // DB transaction) is bigger than the rest of Phase 2 — deferred to a follow-up tracker.
        // TC014 pins the rejection so re-enabling the path is a deliberate change, not a drift.
        if (file.IsDirectory && isCrossDrive)
        {
            return Result<FileItem>.Failure(
                CrossDriveDirectoryUnsupportedCode,
                "Cross-drive directory moves are not supported in v1.5. See follow-up issue.");
        }

        // Target drive lookup runs in both branches (within-drive and cross-drive). Within-drive
        // it's a sanity-check against a freshly soft-deleted drive (which collapses to NotFound
        // via the global query filter); cross-drive it's load-bearing for the provider resolve.
        var targetDrive = await driveRepository.GetByIdAsync(targetDriveId, cancellationToken).ConfigureAwait(false);
        if (targetDrive is null)
        {
            return Result<FileItem>.Failure(NotFoundCode, "Target drive not found.");
        }

        // Collision check on the destination path within the target drive. Soft-deleted rows are
        // excluded by the global query filter, so a previously-deleted target path is reusable
        // without a hard-delete (matches the existing soft-delete contract elsewhere).
        var collision = await fileRepository
            .GetByPathAsync(targetDriveId, targetPath.Value, cancellationToken)
            .ConfigureAwait(false);
        if (collision is not null && collision.Id != file.Id)
        {
            return Result<FileItem>.Failure(ConflictCode, "Target path already exists.");
        }

        // For directory moves, also check the destination prefix has no descendants — otherwise
        // a partial overlap (e.g. moving 'a/dir' onto 'a/renamed' when 'a/renamed/already.txt'
        // exists) would silently merge subtrees in a way no caller asked for. The trailing '/'
        // anchor mirrors GetDescendantsAsync's contract.
        if (file.IsDirectory)
        {
            var targetPrefix = targetPath.Value + "/";
            await foreach (var descendant in fileRepository
                .GetDescendantsAsync(targetDriveId, targetPrefix, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                _ = descendant; // first hit is enough
                return Result<FileItem>.Failure(ConflictCode, "Target directory prefix already occupied.");
            }
        }

        // Branch on (IsDirectory, isCrossDrive). The (true, true) cell was rejected above.
        if (!file.IsDirectory && !isCrossDrive)
        {
            return await MoveFileWithinDriveAsync(file, targetPath, cancellationToken).ConfigureAwait(false);
        }
        if (file.IsDirectory && !isCrossDrive)
        {
            return await MoveDirectoryWithinDriveAsync(file, targetPath, cancellationToken).ConfigureAwait(false);
        }

        // (file:false, cross:true)
        var sourceDrive = await driveRepository.GetByIdAsync(file.DriveId, cancellationToken).ConfigureAwait(false);
        if (sourceDrive is null)
        {
            // Practically unreachable — the file was loaded above, so its drive must exist. Kept
            // defensive: a same-tx soft-delete on the drive between the two reads should still
            // fail gracefully rather than NRE on the cross-drive provider resolve.
            return Result<FileItem>.Failure(NotFoundCode, "Source drive not found.");
        }
        return await MoveFileCrossDriveAsync(file, targetDriveId, targetDrive, sourceDrive, targetPath, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Result<FileItem>> MoveFileWithinDriveAsync(
        FileItem file,
        StoragePath targetPath,
        CancellationToken cancellationToken)
    {
        var oldPath = file.Path;
        var newName = ExtractNameFromPath(targetPath.Value);
        file.MoveTo(file.DriveId, targetPath.Value, newName);

        await publishEndpoint.Publish(
            new FileMovedEvent(
                tenantContext.TenantId,
                file.Id,
                file.DriveId,
                oldPath,
                file.Path,
                currentUser.UserId),
            cancellationToken).ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<FileItem>.Success(file);
    }

    private async Task<Result<FileItem>> MoveDirectoryWithinDriveAsync(
        FileItem file,
        StoragePath targetPath,
        CancellationToken cancellationToken)
    {
        var oldRoot = file.Path;
        var newRoot = targetPath.Value;
        var oldPrefix = oldRoot + "/";

        // Stream descendants and rebase each in place. Mirrors DeleteFileHandler.cs:60-72's
        // soft-delete-the-subtree pattern — single DB transaction, no chunked commits, no torn
        // intermediate state visible to readers.
        await foreach (var descendant in fileRepository
            .GetDescendantsAsync(file.DriveId, oldPrefix, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            descendant.RebaseUnder(oldRoot, newRoot, file.DriveId);
        }

        var newName = ExtractNameFromPath(newRoot);
        file.MoveTo(file.DriveId, newRoot, newName);

        await publishEndpoint.Publish(
            new FileMovedEvent(
                tenantContext.TenantId,
                file.Id,
                file.DriveId,
                oldRoot,
                file.Path,
                currentUser.UserId),
            cancellationToken).ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<FileItem>.Success(file);
    }

    private async Task<Result<FileItem>> MoveFileCrossDriveAsync(
        FileItem file,
        Guid targetDriveId,
        Drive targetDrive,
        Drive sourceDrive,
        StoragePath targetPath,
        CancellationToken cancellationToken)
    {
        // 1. Resolve providers for source and target.
        var sourceProvider = providerRegistry.Resolve(
            sourceDrive.ProviderType,
            DictionaryStorageProviderConfig.FromJson(sourceDrive.ProviderConfig));
        var targetProvider = providerRegistry.Resolve(
            targetDrive.ProviderType,
            DictionaryStorageProviderConfig.FromJson(targetDrive.ProviderConfig));

        // 2. Load the FileVersion row that holds the bytes. file.VersionCount points at the
        // current head — same convention FileDownloadResolver uses. FileVersion is NOT
        // tenanted; reaching it via the tenant-filtered file is the only safe path.
        if (file.VersionCount <= 0)
        {
            return Result<FileItem>.Failure(NotFoundCode, "File has no readable version.");
        }
        var sourceVersion = await fileVersionRepository
            .GetAsync(file.Id, file.VersionCount, cancellationToken)
            .ConfigureAwait(false);
        if (sourceVersion is null)
        {
            return Result<FileItem>.Failure(NotFoundCode, "Source version row missing.");
        }

        var sourceKey = sourceVersion.StorageKey;
        var targetKey = StrgUploadKeys.FinalKey(targetDriveId, file.Id, sourceVersion.VersionNumber);

        // 3. Open plaintext stream (decrypt if source is encrypted).
        Stream plaintextStream;
        FileKey? sourceFileKey = null;
        if (sourceDrive.EncryptionEnabled)
        {
            sourceFileKey = await fileKeyRepository
                .GetByFileVersionAsync(sourceVersion.Id, cancellationToken)
                .ConfigureAwait(false);
            if (sourceFileKey is null)
            {
                return Result<FileItem>.Failure(NotFoundCode, "Source FileKey missing for encrypted drive.");
            }
            plaintextStream = await encryptingWriterFactory
                .Create(sourceProvider)
                .ReadAsync(sourceKey, sourceFileKey.EncryptedDek, sourceFileKey.Algorithm, 0, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            plaintextStream = await sourceProvider
                .ReadAsync(sourceKey, 0, cancellationToken)
                .ConfigureAwait(false);
        }

        // 4. Write to target with fresh key. On any throw between phases 4 and 7, best-effort
        // delete the freshly-written target so we don't strand an unreachable blob — DB
        // transaction has not yet committed so the only state to clean up is on disk.
        byte[]? targetWrappedDek = null;
        string? targetAlgorithm = null;
        long targetBlobSize;
        try
        {
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
                targetBlobSize = file.Size;
            }
        }
        catch
        {
            // Best-effort target-bytes cleanup. Use CancellationToken.None so a caller cancellation
            // doesn't strand the orphan — this branch is already handling a primary failure, the
            // cleanup is a courtesy.
            try
            {
                await targetProvider.DeleteAsync(targetKey, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupEx)
            {
                logger.LogWarning(cleanupEx,
                    "Cross-drive move {FileId}: target cleanup after write failure failed; orphan at {TargetKey}.",
                    file.Id, targetKey);
            }
            throw;
        }
        finally
        {
            await plaintextStream.DisposeAsync().ConfigureAwait(false);
        }

        // 5. DB mutations (staged for the single SaveChangesAsync below).

        // 5a. Reuse FileVersion row via RebaseStorage. Replacing the row would violate the
        // (FileId, VersionNumber) unique index unless we Remove+Add in the same SaveChanges,
        // which is unnecessary churn — the version's logical identity (number + content hash +
        // plaintext size) is unchanged, only the storage envelope flips.
        sourceVersion.RebaseStorage(targetKey, targetBlobSize);

        // 5b. FileKey lifecycle. The unique index on FileKey.FileVersionId is load-bearing — for
        // E→E we MUST Remove + Add in one SaveChangesAsync so EF Core orders DELETE before
        // INSERT, preserving the index invariant without bouncing through the database.
        if (sourceDrive.EncryptionEnabled && !targetDrive.EncryptionEnabled)
        {
            // E → P: drop the source FileKey row (target plaintext drive has no FileKey).
            if (sourceFileKey is not null)
            {
                fileKeyRepository.Remove(sourceFileKey);
            }
        }
        else if (!sourceDrive.EncryptionEnabled && targetDrive.EncryptionEnabled)
        {
            // P → E: insert new FileKey for the target.
            await fileKeyRepository.AddAsync(new FileKey
            {
                FileVersionId = sourceVersion.Id,
                EncryptedDek = targetWrappedDek!,
                Algorithm = targetAlgorithm!,
            }, cancellationToken).ConfigureAwait(false);
        }
        else if (sourceDrive.EncryptionEnabled && targetDrive.EncryptionEnabled)
        {
            // E → E: replace the FileKey row with one that wraps the FRESH DEK. Same DB tx.
            if (sourceFileKey is not null)
            {
                fileKeyRepository.Remove(sourceFileKey);
            }
            await fileKeyRepository.AddAsync(new FileKey
            {
                FileVersionId = sourceVersion.Id,
                EncryptedDek = targetWrappedDek!,
                Algorithm = targetAlgorithm!,
            }, cancellationToken).ConfigureAwait(false);
        }
        // P → P: no FileKey rows touched.

        // 5c. FileItem mutation. StorageKey points at the new bytes; DriveId, Path, Name move
        // in lockstep via MoveTo.
        var oldPath = file.Path;
        file.MoveTo(targetDriveId, targetPath.Value, ExtractNameFromPath(targetPath.Value));
        file.StorageKey = targetKey;

        // 6. Publish FileMovedEvent. Carries the OLD drive id so consumers can attribute the
        // event to the source drive's audit stream (matches DeleteFileHandler's drive-of-origin
        // pattern).
        await publishEndpoint.Publish(
            new FileMovedEvent(
                tenantContext.TenantId,
                file.Id,
                file.DriveId,
                oldPath,
                file.Path,
                currentUser.UserId),
            cancellationToken).ConfigureAwait(false);

        // 7. SaveChangesAsync — atomic for FileItem + FileVersion + FileKey + outbox row.
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // 8. Best-effort source-bytes deletion. Failures are logged with enough context for an
        // operator to clean up; user-visible behaviour is correct because reads now follow the
        // rebased StorageKey on the (committed) FileVersion row. Honest gap captured in the
        // class doc: no reaper covers this prefix today — STRG-040 follow-up.
        try
        {
            await sourceProvider.DeleteAsync(sourceKey, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Cross-drive move {FileId}: source bytes at {SourceKey} could not be deleted; orphan persists. No reaper covers this key prefix today (STRG-040 follow-up).",
                file.Id, sourceKey);
        }

        return Result<FileItem>.Success(file);
    }

    /// <summary>
    /// Extracts the final segment of <paramref name="normalizedPath"/> as the file name. Mirrors
    /// the convention used by <c>CreateFolderHandler</c> and the GraphQL <c>FileMutations.MoveFileAsync</c>:
    /// <c>StoragePath.Parse</c> guarantees no leading or trailing slash, so splitting on
    /// <c>'/'</c> and taking the last non-empty segment is safe.
    /// </summary>
    private static string ExtractNameFromPath(string normalizedPath) =>
        normalizedPath.Split('/').Last(s => s.Length > 0);
}
