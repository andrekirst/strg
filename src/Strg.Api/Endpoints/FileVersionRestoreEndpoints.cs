using System.Security.Claims;
using MassTransit;
using Strg.Api.Auth;
using Strg.Application.Abstractions;
using Strg.Core.Constants;
using Strg.Core.Domain;
using Strg.Core.Events;
using Strg.Core.Identity;
using Strg.Core.Services;
using Strg.Core.Storage;

namespace Strg.Api.Endpoints;

/// <summary>
/// STRG-045 — REST endpoint that restores a file to a previous version. Restore is implemented
/// as <i>append a new version that copies the old version's content</i>, NOT as a destructive
/// rollback. The full version history is preserved, and the restored content is exposed as
/// <c>versionNumber = currentMax + 1</c> — the same shape a fresh upload would produce.
///
/// <para><b>Pipeline.</b> 1) tenant-filtered <see cref="FileItem"/> lookup (mismatched
/// <c>driveId</c> collapsed to 404 to deny cross-drive enumeration); 2)
/// <see cref="IFileVersionStore.GetVersionAsync"/> for the source version (transitive tenant
/// gate via the <c>FileItem</c> lookup the store performs internally); 3) drive provider
/// resolution; 4) stream the source blob to a freshly-derived storage key
/// (<see cref="StrgUploadKeys.FinalKey"/> with the next version number); 5)
/// <see cref="IFileVersionStore.CreateVersionAsync"/> appends the new <c>FileVersion</c>,
/// updates <c>FileItem.Size/ContentHash/StorageKey/VersionCount</c>, and commits quota in a
/// single transaction; 6) outbox publish of <see cref="FileUploadedEvent"/> followed by a
/// flushing <see cref="IStrgDbContext.SaveChangesAsync"/> so the outbox row reaches the
/// dispatcher.</para>
///
/// <para><b>Why a NEW version, not an in-place overwrite.</b> A destructive rollback would
/// silently delete the user's intervening edits — the issue's "Restore does not delete any
/// version records" code-review checklist pins the no-delete invariant. Appending a new
/// version mirrors how source-control restoration works: the old commit lives on, the
/// "current" pointer moves forward via a forward-progress write.</para>
///
/// <para><b>Why a derived storage key.</b> <see cref="StrgUploadKeys.FinalKey"/> is anchored
/// on the immutable <c>FileItem.Id</c> + the new <c>VersionNumber</c>, so the new blob lives
/// at a key distinct from the source version's storage key. The source blob is never
/// mutated — a future read of <c>versionNumber=N</c> still hits the original bytes. An
/// alternative shape (write to <c>file.Path</c> directly, as the issue's handler sketch
/// suggests) would mix logical paths into the storage layer, defeating the rename-is-pure-DB
/// property documented on <see cref="StrgUploadKeys.FinalKey"/>.</para>
///
/// <para><b>Race against concurrent uploads.</b> The pre-computed <c>nextVersionNumber</c>
/// (max + 1) drives the storage key, and <see cref="IFileVersionStore.CreateVersionAsync"/>
/// re-computes the same value under its own transaction. If a competing upload commits
/// between these two reads, both writes target the same <c>VersionNumber</c> and the
/// <c>(FileId, VersionNumber)</c> unique index throws on the loser — the loser's transaction
/// rolls back atomically. The blob written under the contested key remains as an orphan
/// reaped by the cleanup job (STRG-026 #2), never as a row pointing at vanished bytes.
/// Surfacing this as a 5xx today is acceptable; v0.2 may add explicit retry.</para>
///
/// <para><b>Tenant isolation</b> flows from the global query filters on
/// <see cref="IFileRepository.GetByIdAsync"/> + the store's transitive
/// <see cref="IFileVersionStore.GetVersionAsync"/> guard.
/// <c>RequireAuthorization(AuthPolicies.FilesWrite)</c> stops scope-deficient callers with
/// HTTP 403 before the handler runs.</para>
/// </summary>
public static class FileVersionRestoreEndpoints
{
    public static IEndpointRouteBuilder MapFileVersionRestoreEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/api/v1/drives/{driveId:guid}/files/{fileId:guid}/versions/{versionNumber:int}/restore",
                RestoreVersionAsync)
            .RequireAuthorization(AuthPolicies.FilesWrite)
            .WithName("RestoreFileVersion")
            .WithTags("Files")
            .WithSummary("Restore a file to a previous version (creates a new version).")
            .WithDescription(
                "Copies the bytes of an existing version to a NEW version of the same file " +
                "(version_number = current_max + 1). The complete version history is preserved " +
                "— no version records are deleted. The new version becomes the file's current " +
                "content; subsequent downloads serve the restored bytes. Returns 404 when the " +
                "file or the requested source version does not exist (or belongs to a different " +
                "drive). Requires the files.write scope.");

        return app;
    }

    private static async Task<IResult> RestoreVersionAsync(
        Guid driveId,
        Guid fileId,
        int versionNumber,
        IFileVersionStore versionStore,
        IFileRepository fileRepo,
        IFileVersionRepository versionRepo,
        IDriveRepository driveRepo,
        IStorageProviderRegistry registry,
        IPublishEndpoint publishEndpoint,
        IStrgDbContext db,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        // Tenant-filtered lookup. A wrong-drive id is collapsed to 404 (NOT 403) so the wire
        // shape cannot enumerate which drive a file belongs to — same stance as the delete and
        // download endpoints.
        var file = await fileRepo.GetByIdAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (file is null || file.DriveId != driveId)
        {
            return Results.NotFound();
        }

        if (file.IsDirectory)
        {
            return Results.NotFound();
        }

        // GetVersionAsync filters by (fileId, versionNumber) AND validates the file's tenant via
        // its internal fileRepo lookup — a caller in tenant A cannot probe version-existence in
        // tenant B by guessing fileId. Cross-tenant lookups return null.
        var sourceVersion = await versionStore
            .GetVersionAsync(fileId, versionNumber, cancellationToken)
            .ConfigureAwait(false);
        if (sourceVersion is null)
        {
            return Results.NotFound();
        }

        var drive = await driveRepo.GetByIdAsync(file.DriveId, cancellationToken).ConfigureAwait(false);
        if (drive is null)
        {
            // Belt-and-braces: a tenant-isolated GetByIdAsync that returns null after we already
            // resolved the file by driveId implies a soft-deleted drive whose files weren't
            // cascade-deleted. Surface as 404 — restoring into a vanished drive is incoherent.
            return Results.NotFound();
        }

        var providerConfig = DictionaryStorageProviderConfig.FromJson(drive.ProviderConfig);
        var provider = registry.Resolve(drive.ProviderType, providerConfig);

        // Pre-compute the new version number from the current max. CreateVersionAsync re-reads
        // and assigns the same value under its own transaction; the (FileId, VersionNumber)
        // unique index protects against concurrent uploads racing for the same number.
        var existingVersions = await versionRepo.ListAsync(fileId, cancellationToken).ConfigureAwait(false);
        var nextVersionNumber = existingVersions.Count == 0
            ? 1
            : existingVersions.Max(v => v.VersionNumber) + 1;

        var newStorageKey = StrgUploadKeys.FinalKey(driveId, fileId, nextVersionNumber);

        // Stream copy: ReadAsync returns an open stream, WriteAsync consumes it without
        // buffering. CLAUDE.md's "Never buffer large files in memory" rule plus the
        // IStorageProvider.WriteAsync contract ("MUST NOT buffer the entire stream in memory")
        // are honoured by the source provider's chunked read + the destination provider's
        // chunked write — both target the same provider here, but the contract holds even on
        // future cross-provider restores.
        await using (var sourceStream = await provider
            .ReadAsync(sourceVersion.StorageKey, offset: 0, cancellationToken)
            .ConfigureAwait(false))
        {
            await provider.WriteAsync(newStorageKey, sourceStream, cancellationToken).ConfigureAwait(false);
        }

        // Append the new version. The store: (a) re-computes nextNumber under its own tx and
        // assigns it to the row, (b) updates FileItem.Size/ContentHash/StorageKey/VersionCount
        // to the restored values, (c) charges plaintext size against the file owner's quota,
        // (d) commits all of the above in one DbContext transaction. The size + contentHash
        // arguments come straight from the source version so the restored file is byte-for-byte
        // identical to the historical state.
        var userId = user.GetUserId();
        await versionStore.CreateVersionAsync(
            file,
            storageKey: newStorageKey,
            contentHash: sourceVersion.ContentHash,
            size: sourceVersion.Size,
            blobSizeBytes: sourceVersion.BlobSizeBytes,
            createdBy: userId,
            cancellationToken).ConfigureAwait(false);

        // Outbox publish. CreateVersionAsync committed its own tx already, so this Publish lands
        // a fresh outbox row whose flushing SaveChangesAsync follows immediately below. The
        // dual-write window between the version commit and this outbox flush is bounded to a
        // single request; a process crash mid-window leaves a durable version with no
        // FileUploadedEvent, accepted by the spec (the audit trail still captures the version
        // row itself; downstream consumers can be back-filled from FileVersion.CreatedAt).
        await publishEndpoint.Publish(
            new FileUploadedEvent(
                file.TenantId,
                file.Id,
                file.DriveId,
                userId,
                sourceVersion.Size,
                file.MimeType),
            cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(ToDto(file));
    }

    private static FileItemDto ToDto(FileItem f) => new(
        f.Id,
        f.Name,
        f.Path,
        f.Size,
        f.MimeType,
        f.IsDirectory,
        f.ContentHash,
        f.CreatedAt,
        f.UpdatedAt);
}
