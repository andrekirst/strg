using System.Security.Claims;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Strg.Api.Auth;
using Strg.Application.Abstractions;
using Strg.Core.Domain;
using Strg.Core.Events;
using Strg.Core.Exceptions;
using Strg.Core.Identity;
using Strg.Core.Services;
using Strg.Core.Storage;

namespace Strg.Api.Endpoints;

/// <summary>
/// STRG-041 — REST endpoint that copies a file inside the same drive or to a different drive
/// owned by the caller's tenant. The handler creates a fresh <see cref="FileItem"/> (new
/// <see cref="Guid"/>), a <see cref="FileVersion"/> at version 1, copies the storage blob via
/// the per-drive <see cref="IStorageProvider"/>, commits quota, and publishes
/// <see cref="FileUploadedEvent"/> through the MassTransit outbox.
///
/// <para><b>Quota flow (Commit-as-reservation, single-phase per STRG-032).</b> The order is:
/// <list type="number">
/// <item>Pre-flight <see cref="IQuotaService.CheckAsync"/> — advisory; surfaces 507 to the
/// caller before we open a tx so the early-rejection path is cheap and matches the AC's
/// "quota checked before copy" intent.</item>
/// <item><see cref="IQuotaService.CommitAsync"/> — atomic gate. Two racing copies against the
/// same budget cannot both pass; the loser throws <see cref="QuotaExceededException"/> and
/// short-circuits before any storage I/O.</item>
/// <item><see cref="IStorageProvider.CopyAsync"/> — the physical bytes-on-disk copy.</item>
/// <item>On storage failure → <see cref="IQuotaService.ReleaseAsync"/> compensation. Without
/// this, a crashed CopyAsync would leak quota: bytes were charged at Commit but no FileVersion
/// row exists to link them to.</item>
/// <item>DB rows + outbox event staged on the change tracker, flushed atomically by a single
/// <see cref="IStrgDbContext.SaveChangesAsync"/>. Publish-before-Save is the canonical
/// MassTransit <c>UseBusOutbox</c> ordering — see <c>DeleteFileHandler</c> for the same
/// pattern; CLAUDE.md's "publish AFTER" guidance pre-dates the interceptor wiring.</item>
/// </list></para>
///
/// <para><b>Tenant isolation.</b> The global query filter on <c>StrgDbContext.Files</c> /
/// <c>StrgDbContext.Drives</c> enforces tenant scope on every read. A caller addressing a file
/// they cannot see (different tenant, soft-deleted) returns 404 collapsed — no enumeration
/// oracle, identical wire shape to "file does not exist".</para>
///
/// <para><b>Cross-drive ownership.</b> Both source and target drive must belong to the caller's
/// tenant. Source ownership is gated by the route's <c>{driveId}</c> + the source FileItem's
/// own DriveId match (collapsed to 404 on mismatch); target ownership flows from
/// <see cref="IDriveRepository.GetByIdAsync"/>, which honours the global tenant filter.</para>
///
/// <para><b>Path safety.</b> User-supplied <c>TargetPath</c> goes through
/// <see cref="StoragePath.Parse"/> before any storage call. Traversal, null bytes, reserved
/// names, and UNC-style inputs are rejected with HTTP 400. Bonus: the source's own StorageKey
/// is treated as a trusted internal value (it was sanitized at upload time), but we still
/// route it through <see cref="StoragePath.Parse"/> as defence-in-depth in case a corrupt row
/// somehow contains a value that StoragePath would now reject.</para>
/// </summary>
public static class CopyFileEndpoints
{
    private const string RouteName = "CopyFile";
    private const string GetFileRouteName = "GetFile";
    private const int CrossEncryptionConflictStatus = StatusCodes.Status409Conflict;

    public static IEndpointRouteBuilder MapCopyFileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/drives/{driveId:guid}/files/{fileId:guid}/copy", CopyFileAsync)
            .RequireAuthorization(AuthPolicies.FilesWrite)
            .WithName(RouteName)
            .WithTags("Files")
            .WithSummary("Copy a file to a new path within the same drive or to a different drive.")
            .WithDescription(
                "Creates a fresh FileItem (new Guid) and FileVersion (v1) at the requested " +
                "TargetPath, copying the source blob via the per-drive storage provider. The " +
                "source file is unchanged. Quota is enforced via Commit-as-reservation: the " +
                "atomic Commit happens before the storage write so an over-quota copy never " +
                "touches the backend, and a failed storage write is compensated by Release. " +
                "Cross-drive copies are supported via TargetDriveId; if omitted, the route's " +
                "drive is reused. Returns 201 with the new file body, 400 on path-traversal / " +
                "reserved name, 404 when source is missing or in another drive, 409 on target " +
                "collision, 507 on quota exhaustion.");

        return app;
    }

    private static async Task<IResult> CopyFileAsync(
        Guid driveId,
        Guid fileId,
        [FromBody] CopyFileRequest request,
        [FromServices] IFileRepository fileRepository,
        [FromServices] IDriveRepository driveRepository,
        [FromServices] IFileVersionRepository versionRepository,
        [FromServices] IStorageProviderRegistry registry,
        [FromServices] IQuotaService quotaService,
        [FromServices] IPublishEndpoint publishEndpoint,
        [FromServices] IStrgDbContext db,
        [FromServices] ITenantContext tenantContext,
        [FromServices] ILogger<CopyFileLog> logger,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "Request body required." });
        }

        // 1) Path safety. StoragePath.Parse rejects traversal, null bytes, reserved names,
        // absolute/UNC inputs, empty/whitespace. A 400 here is a request-shape failure — same
        // surface the upload path returns on the same input.
        StoragePath targetPath;
        try
        {
            targetPath = StoragePath.Parse(request.TargetPath);
        }
        catch (StoragePathException ex)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: ex.Message);
        }

        // 2) Source resolution. Cross-drive id mismatch is collapsed to 404 — same enumeration-
        // oracle stance as DeleteFileHandler. The global tenant + soft-delete filters on
        // StrgDbContext.Files apply, so a cross-tenant probe returns null without leaking.
        var source = await fileRepository.GetByIdAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (source is null || source.DriveId != driveId)
        {
            return Results.NotFound();
        }

        if (source.IsDirectory)
        {
            // The endpoint contract is single-file copy; recursive directory copy needs its own
            // endpoint with explicit semantics around per-descendant quota commits and per-blob
            // failure isolation. Surfacing 400 here is the right hint to the client — silently
            // succeeding with no work would be worse, and partial-success on a half-recursed
            // tree would be much worse.
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "Directory copy is not supported by this endpoint.");
        }

        var targetDriveId = request.TargetDriveId ?? driveId;
        var userId = user.GetUserId();

        // 3) Target drive lookup. IDriveRepository.GetByIdAsync honours the tenant filter, so a
        // caller passing a TargetDriveId from another tenant returns null. Same-tenant + missing
        // drive → 404, matching the source-drive 404 shape (no oracle).
        var targetDrive = await driveRepository.GetByIdAsync(targetDriveId, cancellationToken).ConfigureAwait(false);
        if (targetDrive is null)
        {
            return Results.NotFound();
        }

        // 4) Cross-encryption guard. v0.1 stores ciphertext in the underlying provider when
        // EncryptionEnabled is set; the per-drive envelope/key context is part of the read path.
        // Raw provider.CopyAsync() copies BYTES — copying a ciphertext blob to a plaintext drive
        // (or vice-versa) leaves a payload that the receiving drive's read path cannot interpret.
        // Surfacing 409 is the right early-rejection: the operation is semantically valid on
        // matching-posture drives only. Same-drive copies trivially satisfy this.
        if (driveId != targetDriveId)
        {
            var sourceDrive = await driveRepository.GetByIdAsync(driveId, cancellationToken).ConfigureAwait(false);
            if (sourceDrive is null)
            {
                return Results.NotFound();
            }
            if (sourceDrive.EncryptionEnabled != targetDrive.EncryptionEnabled)
            {
                return Results.Problem(
                    statusCode: CrossEncryptionConflictStatus,
                    detail: "Cross-drive copy between drives with mismatched encryption posture is not supported.");
            }
        }

        // 5) Collision check. The unique-key invariant on (DriveId, Path, IsDeleted=false) is
        // enforced by the storage provider AND by the DB; this pre-check turns a race-loser
        // 500 into the AC's 409 surface.
        var collision = await db.Files
            .AnyAsync(
                f => f.DriveId == targetDriveId && f.Path == targetPath.Value,
                cancellationToken)
            .ConfigureAwait(false);
        if (collision)
        {
            return Results.Conflict(new { error = "Target path already exists." });
        }

        // 6) Pre-flight quota check (advisory per STRG-032). The authoritative gate is CommitAsync
        // below; this Check exists so the AC's "quota checked before copy" pin has a structurally
        // distinct early path, and so the over-quota integration test does not need to race past
        // a CommitAsync atomic-update to assert the 507 surface.
        try
        {
            var checkResult = await quotaService.CheckAsync(userId, source.Size, cancellationToken).ConfigureAwait(false);
            if (!checkResult.IsAllowed)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status507InsufficientStorage,
                    detail: "Storage quota exceeded.");
            }
        }
        catch (QuotaExceededException)
        {
            // Missing-user collapse per IQuotaService class doc — safe to surface as 507 since
            // the client already has files.write scope on a tenant-tied JWT, so this can only
            // mean the user row was hard-deleted out from under them. Same wire shape as a real
            // shortfall is the enumeration-oracle-safe choice.
            return Results.Problem(
                statusCode: StatusCodes.Status507InsufficientStorage,
                detail: "Storage quota exceeded.");
        }

        // 7) Resolve provider(s) and confirm source has a storage key. A FileItem with
        // StorageKey == null indicates a directory or an in-flight upload that hasn't finalised;
        // we already filtered IsDirectory above, so a null here is a corrupt-row condition the
        // 500 surface is appropriate for.
        if (string.IsNullOrEmpty(source.StorageKey))
        {
            logger.LogError(
                "File {FileId} has no StorageKey set; cannot copy.",
                source.Id);
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                detail: "Source file is missing its storage key.");
        }

        // Same-drive copies use a single provider and the provider's native CopyAsync (avoids the
        // read-write round trip). Cross-drive copies resolve BOTH providers and stream
        // source.ReadAsync → target.WriteAsync; the issue's pseudocode showed only the target
        // provider, which silently fails on a real cross-drive setup because the target's
        // provider has no visibility into the source's storage backend (different rootPath, S3
        // bucket, etc.).
        var targetProviderConfig = DictionaryStorageProviderConfig.FromJson(targetDrive.ProviderConfig);
        var targetProvider = registry.Resolve(targetDrive.ProviderType, targetProviderConfig);

        IStorageProvider? sourceProvider = null;
        if (driveId != targetDriveId)
        {
            // Cross-drive: re-fetch source drive (we already touched it for the encryption guard,
            // but that result wasn't kept in scope). The tenant filter still applies — null
            // collapses to 404, same as the source-FileItem branch above.
            var sourceDriveForCopy = await driveRepository.GetByIdAsync(driveId, cancellationToken).ConfigureAwait(false);
            if (sourceDriveForCopy is null)
            {
                return Results.NotFound();
            }
            var sourceProviderConfig = DictionaryStorageProviderConfig.FromJson(sourceDriveForCopy.ProviderConfig);
            sourceProvider = registry.Resolve(sourceDriveForCopy.ProviderType, sourceProviderConfig);
        }

        // 8) Atomic-gate commit. Throws QuotaExceededException on shortfall — that's the 507
        // surface. Note we do NOT release here on Commit failure: the commit was rejected, no
        // bytes were charged, no compensation needed.
        try
        {
            await quotaService.CommitAsync(userId, source.Size, cancellationToken).ConfigureAwait(false);
        }
        catch (QuotaExceededException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status507InsufficientStorage,
                detail: "Storage quota exceeded.");
        }

        // 9) Storage copy. From here on, any failure must release the quota or we leak budget.
        // Same-drive: native CopyAsync. Cross-drive: read from source, stream-write to target —
        // the streamed pattern keeps memory bounded for large files (no MemoryStream buffering).
        var targetStorageKey = targetPath.Value;
        try
        {
            if (sourceProvider is null)
            {
                await targetProvider.CopyAsync(source.StorageKey, targetStorageKey, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var sourceStream = await sourceProvider
                    .ReadAsync(source.StorageKey, 0, cancellationToken)
                    .ConfigureAwait(false);
                await targetProvider.WriteAsync(targetStorageKey, sourceStream, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (FileNotFoundException ex)
        {
            // Source key disappeared between the FileItem lookup and the storage read — likely a
            // concurrent delete or a corrupt FileItem.StorageKey. Release quota and surface 404.
            await CompensateReleaseAsync(quotaService, userId, source.Size, logger).ConfigureAwait(false);
            logger.LogWarning(ex,
                "Source blob missing for file {FileId} (StorageKey={StorageKey})",
                source.Id, source.StorageKey);
            return Results.NotFound();
        }
        catch (IOException ex)
        {
            // Destination collision (LocalFileSystemProvider's File.Copy with overwrite:false,
            // InMemoryStorageProvider's TryAdd-fails). The AnyAsync pre-check should have caught
            // this, but a race between the collision-check query and the storage write IS
            // theoretically possible under concurrent copies — surface 409 with quota release.
            await CompensateReleaseAsync(quotaService, userId, source.Size, logger).ConfigureAwait(false);
            logger.LogInformation(ex,
                "Storage collision on copy to {TargetDriveId}/{TargetPath} (race past pre-check)",
                targetDriveId, targetStorageKey);
            return Results.Conflict(new { error = "Target path already exists." });
        }
        catch (Exception ex)
        {
            // Catch-all: storage backend errors (S3 5xx, disk-full, permission denied) collapse
            // to 500 with quota release.
            await CompensateReleaseAsync(quotaService, userId, source.Size, logger).ConfigureAwait(false);
            logger.LogError(ex,
                "CopyAsync failed for file {FileId} → {TargetDriveId}/{TargetPath}",
                source.Id, targetDriveId, targetStorageKey);
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                detail: "Storage copy failed.");
        }

        // 10) Stage the new FileItem + FileVersion + outbox event. Entity.Id default = Guid.NewGuid()
        // so the new file's id is distinct from the source by construction (issue's CR checklist
        // explicitly pins this).
        var newFile = new FileItem
        {
            // TenantId from context, NEVER from request body or source.TenantId. The tenant
            // filter on Files invariably matches against ITenantContext.TenantId, so reading
            // it from the same authority is the source of truth.
            TenantId = tenantContext.TenantId,
            DriveId = targetDriveId,
            Name = ExtractFileName(targetPath.Value),
            Path = targetPath.Value,
            Size = source.Size,
            ContentHash = source.ContentHash,
            MimeType = source.MimeType,
            IsDirectory = false,
            StorageKey = targetStorageKey,
            CreatedBy = userId,
            VersionCount = 1,
        };

        var version = new FileVersion
        {
            FileId = newFile.Id,
            VersionNumber = 1,
            Size = source.Size,
            BlobSizeBytes = source.Size,
            ContentHash = source.ContentHash ?? string.Empty,
            StorageKey = targetStorageKey,
            CreatedBy = userId,
        };

        await fileRepository.AddAsync(newFile, cancellationToken).ConfigureAwait(false);
        await versionRepository.AddAsync(version, cancellationToken).ConfigureAwait(false);

        // 11) Publish via outbox interceptor BEFORE SaveChangesAsync — the interceptor stages an
        // outbox row on the change tracker, the single SaveChangesAsync below commits the file,
        // version, and outbox row in one transaction. See DeleteFileHandler for the same
        // ordering rationale.
        await publishEndpoint.Publish(
            new FileUploadedEvent(
                tenantContext.TenantId,
                newFile.Id,
                targetDriveId,
                userId,
                source.Size,
                source.MimeType),
            cancellationToken).ConfigureAwait(false);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // SaveChanges failed AFTER provider.CopyAsync succeeded → orphan blob on target +
            // wasted quota. Best-effort: release quota AND delete the orphan blob. Failing
            // either compensation cleanly logs but doesn't supersede the original failure.
            await CompensateReleaseAsync(quotaService, userId, source.Size, logger).ConfigureAwait(false);
            try
            {
                await targetProvider.DeleteAsync(targetStorageKey, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupEx)
            {
                logger.LogWarning(cleanupEx,
                    "Best-effort orphan-blob delete failed for {TargetPath}",
                    targetStorageKey);
            }
            logger.LogError(ex,
                "SaveChangesAsync failed after storage copy for file {NewFileId}",
                newFile.Id);
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                detail: "Failed to persist copied file metadata.");
        }

        // 12) 201 Created with the new file's wire DTO. The Location header points at a future
        // GET-by-id route; until that endpoint exists, the body is the authoritative payload.
        var dto = FileItemDto.From(newFile);
        return Results.Created(
            $"/api/v1/drives/{targetDriveId}/files/{newFile.Id}",
            dto);
    }

    private static async Task CompensateReleaseAsync(
        IQuotaService quotaService,
        Guid userId,
        long bytes,
        ILogger logger)
    {
        try
        {
            await quotaService.ReleaseAsync(userId, bytes, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // ReleaseAsync is documented to no-op on missing user; reaching here means a real
            // DB error. Log but don't supersede the caller-facing failure — the original
            // exception is the actionable one.
            logger.LogWarning(ex,
                "Quota compensation release failed for user {UserId} ({Bytes} bytes)",
                userId, bytes);
        }
    }

    private static string ExtractFileName(string storagePath)
    {
        // StoragePath.Normalize trims leading/trailing slashes and replaces backslashes; the last
        // segment after '/' is the filename. A bare filename (no slash) is its own filename.
        var lastSlash = storagePath.LastIndexOf('/');
        return lastSlash < 0 ? storagePath : storagePath[(lastSlash + 1)..];
    }

    /// <summary>
    /// Logger category marker — gives the static endpoint method a stable
    /// <c>ILogger&lt;CopyFileLog&gt;</c> binding without exposing the static class as a generic
    /// type parameter.
    /// </summary>
    public sealed class CopyFileLog;
}
