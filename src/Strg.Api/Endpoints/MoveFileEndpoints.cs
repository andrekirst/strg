using System.Security.Claims;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Strg.Api.Auth;
using Strg.Application.Abstractions;
using Strg.Core.Domain;
using Strg.Core.Events;
using Strg.Core.Identity;
using Strg.Core.Storage;

namespace Strg.Api.Endpoints;

/// <summary>
/// STRG-040 — REST endpoint that moves a <see cref="FileItem"/> to a new path, optionally
/// rebinding it to a different drive in the same tenant. The endpoint is a thin protocol shim
/// on the surface (route binding, request DTO, mapping the four spec'd outcomes onto HTTP
/// status codes) — the move itself runs inline because the move flow is short, has no
/// reusable handler today, and the spec mandates a direct injection of the four collaborators.
///
/// <para><b>Tenant isolation</b> is the global query filter on
/// <see cref="IStrgDbContext.Files"/> and <c>Drives</c> — the endpoint cannot bypass it.
/// <see cref="AuthPolicies.FilesWrite"/> blocks scope-deficient callers with HTTP 403 before
/// the handler runs. A file whose <see cref="FileItem.DriveId"/> does not match the route is
/// collapsed to 404 inside the handler so the wire response cannot be used as an enumeration
/// oracle for files in other drives.</para>
///
/// <para><b>Storage-vs-DB ordering (issue Code Review Checklist resolution).</b> The issue
/// body lists two contradictory bullet points about whether the storage <c>MoveAsync</c>
/// runs before or after <see cref="IStrgDbContext.SaveChangesAsync"/>. We pick
/// <b>DB-first, then storage</b>: stage the row mutation + outbox row, commit them in one
/// SaveChangesAsync, then run the storage MoveAsync. Rationale: a SaveChangesAsync failure
/// (validation, concurrency, FK) leaves storage untouched — the easy half of the
/// recovery problem. A storage MoveAsync failure AFTER a successful DB commit is the harder
/// half: we log loudly so an operator can surface it, and a future compensation event can
/// re-anchor the orphaned row. We do NOT silently revert the DB commit because doing so
/// would require a second SaveChangesAsync that would itself stage another outbox publish
/// of FileMovedEvent (now with the OLD path as the new path), confusing every downstream
/// consumer. Compensation lives in a follow-up issue, not in this commit's scope.</para>
///
/// <para><b>Outbox publish ordering.</b> <c>IPublishEndpoint.Publish</c> is called BEFORE
/// SaveChangesAsync. MassTransit's <c>UseBusOutbox()</c> interceptor stages the publish on the
/// change tracker as an outbox row; the single subsequent SaveChangesAsync commits the row
/// mutation and the outbox row in one transaction. CLAUDE.md's "publish AFTER
/// SaveChangesAsync" guidance pre-dates the <c>UseBusOutbox()</c> wiring; cross-reference
/// <see cref="Strg.Application.Features.Files.Delete.DeleteFileHandler"/> for the canonical
/// publish-before-save pattern.</para>
/// </summary>
public static class MoveFileEndpoints
{
    public static IEndpointRouteBuilder MapMoveFileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/drives/{driveId:guid}/files/{fileId:guid}/move", MoveFileAsync)
            .RequireAuthorization(AuthPolicies.FilesWrite)
            .WithName("MoveFile")
            .WithTags("Files")
            .WithSummary("Move a file or directory to a new path, optionally in a different drive.")
            .WithDescription(
                "Moves the target file or directory to <c>targetPath</c>. When " +
                "<c>targetDriveId</c> is supplied the file is rebound to that drive (same " +
                "tenant only); when omitted the move stays in the source drive. Returns 400 " +
                "on invalid paths (traversal, null bytes, reserved names), 404 on missing " +
                "source/target drive or cross-drive id mismatch, and 409 when the target " +
                "path is already occupied. <c>FileMovedEvent</c> is published via the outbox " +
                "after the DB transaction commits.");

        return app;
    }

    private static async Task<IResult> MoveFileAsync(
        Guid driveId,
        Guid fileId,
        [FromBody] MoveFileRequest request,
        IStrgDbContext db,
        IFileRepository fileRepository,
        IDriveRepository driveRepository,
        IStorageProviderRegistry providerRegistry,
        IPublishEndpoint publishEndpoint,
        ClaimsPrincipal user,
        ILogger<MoveFileLog> logger,
        CancellationToken cancellationToken)
    {
        // StoragePath.Parse is the parse-time gate for traversal/null-byte/reserved-name
        // attacks. A failure here MUST return 400 before any DB or storage call — never log
        // the raw input at info level (it's untrusted). Mirrors the GraphQL FileMutations
        // pattern.
        StoragePath targetPath;
        try
        {
            targetPath = StoragePath.Parse(request.TargetPath);
        }
        catch (StoragePathException ex)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid target path.",
                detail: ex.Message);
        }

        var file = await fileRepository.GetByIdAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (file is null || file.DriveId != driveId)
        {
            // Cross-drive id mismatch is collapsed to 404 (NOT 403) so the wire shape cannot
            // enumerate which drive a file belongs to. Same security stance as DeleteFileHandler
            // and FileDownloadResolver.
            return Results.NotFound();
        }

        var targetDriveId = request.TargetDriveId ?? driveId;
        var targetDrive = await driveRepository.GetByIdAsync(targetDriveId, cancellationToken).ConfigureAwait(false);
        if (targetDrive is null)
        {
            return Results.NotFound();
        }

        // Collision check against the target (driveId, path) before any storage I/O. The
        // global tenant + soft-delete query filters on the Files DbSet apply, so a
        // soft-deleted row at the same path will NOT block the move — that's the intended
        // semantics (deleted rows are tombstones, not occupants).
        var existing = await fileRepository
            .GetByPathAsync(targetDriveId, targetPath.Value, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return Results.Conflict(new { error = "Target path already exists." });
        }

        var oldPath = file.Path;
        var isCrossDriveMove = targetDriveId != driveId;

        // Source drive lookup: we need it for cross-drive moves so we can read bytes from the
        // source provider. For same-drive moves the source IS the target, so we skip the
        // extra lookup.
        Drive? sourceDrive = null;
        if (isCrossDriveMove)
        {
            sourceDrive = await driveRepository
                .GetByIdAsync(driveId, cancellationToken)
                .ConfigureAwait(false);
            if (sourceDrive is null)
            {
                // Source drive was the route's {driveId}; if it's missing the file shouldn't
                // have resolved either. Defensive 404 to avoid a 500 on the rare race where
                // the drive is soft-deleted between the file lookup and here.
                return Results.NotFound();
            }
        }

        var targetProviderConfig = DictionaryStorageProviderConfig.FromJson(targetDrive.ProviderConfig);
        var targetProvider = providerRegistry.Resolve(targetDrive.ProviderType, targetProviderConfig);

        // Mutate row state.
        file.Path = targetPath.Value;
        file.Name = targetPath.Value.Split('/').Last(s => s.Length > 0);
        file.DriveId = targetDriveId;

        // Publish-before-save: UseBusOutbox stages this as an outbox row on the change tracker;
        // the single SaveChangesAsync below commits the row + outbox in one transaction.
        await publishEndpoint.Publish(
            new FileMovedEvent(
                user.GetTenantId(),
                fileId,
                targetDriveId,
                oldPath,
                targetPath.Value,
                user.GetUserId()),
            cancellationToken).ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Storage move runs AFTER the DB commit. If it throws here, the row is already at the
        // new (DriveId, Path) but the bytes still live at the old key — log it loudly so an
        // operator can re-anchor the row or rerun the storage move. A compensation event is
        // out-of-scope for this issue (see class doc for rationale).
        try
        {
            if (isCrossDriveMove)
            {
                // Cross-drive move: each drive has its own provider with its own root, so a
                // single-provider MoveAsync would be addressing the wrong root for the
                // source. Stream-copy from the source provider to the target provider, then
                // delete from the source. Streaming (NOT bytes/MemoryStream) so large files
                // don't materialize in memory — see CLAUDE.md "never buffer large files".
                var sourceProviderConfig = DictionaryStorageProviderConfig.FromJson(sourceDrive!.ProviderConfig);
                var sourceProvider = providerRegistry.Resolve(sourceDrive.ProviderType, sourceProviderConfig);

                await using var sourceStream = await sourceProvider
                    .ReadAsync(oldPath, offset: 0, cancellationToken)
                    .ConfigureAwait(false);
                await targetProvider
                    .WriteAsync(targetPath.Value, sourceStream, cancellationToken)
                    .ConfigureAwait(false);
                await sourceProvider
                    .DeleteAsync(oldPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await targetProvider
                    .MoveAsync(oldPath, targetPath.Value, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Storage move failed AFTER DB commit. File={FileId} OldPath={OldPath} NewPath={NewPath} TargetDriveId={TargetDriveId} CrossDrive={CrossDrive}",
                fileId, oldPath, targetPath.Value, targetDriveId, isCrossDriveMove);
            throw;
        }

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

    /// <summary>
    /// Logger category marker — gives the static endpoint method a stable
    /// <see cref="ILogger{TCategoryName}"/> binding without exposing the static class as a
    /// generic type parameter.
    /// </summary>
    public sealed class MoveFileLog;
}
