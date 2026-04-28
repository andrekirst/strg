using Mediator;
using Strg.Api.Auth;
using Strg.Application.Features.Files.Move;
using Strg.Core.Domain;

namespace Strg.Api.Endpoints;

/// <summary>
/// STRG-040 — REST endpoint that moves a <see cref="FileItem"/> to a new path. The endpoint is a
/// thin protocol shim: route binding, body deserialization, dispatch via <see cref="IMediator"/>,
/// and a status-code mapping on the <see cref="Strg.Core.Result{T}"/> outcome. All business logic
/// — drive/file resolution, path parsing, collision detection, outbox publish, audit emission via
/// consumer — lives in <see cref="MoveFileHandler"/>.
///
/// <para><b>Tenant isolation</b> is the global query filter on <c>StrgDbContext</c>'s file and
/// drive sets — the endpoint cannot bypass it. <c>RequireAuthorization(AuthPolicies.FilesWrite)</c>
/// stops scope-deficient callers with HTTP 403 before the handler runs. A file whose
/// <c>DriveId</c> does not match the route is collapsed to 404 inside the handler so the wire
/// response cannot be used as an enumeration oracle for files in other drives.</para>
///
/// <para><b>v1.5 deferral.</b> Cross-drive directory moves return HTTP 400
/// <c>CrossDriveDirectoryUnsupported</c> in v1.5 — see follow-up issue. Within-drive
/// directories, within-drive single files, and cross-drive single files are all enabled.</para>
/// </summary>
public static class FileMoveEndpoints
{
    public static IEndpointRouteBuilder MapFileMoveEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/drives/{driveId:guid}/files/{fileId:guid}/move", MoveFileAsync)
            .RequireAuthorization(AuthPolicies.FilesWrite)
            .WithName("MoveFile")
            .WithTags("Files")
            .WithSummary("Move a file or directory to a new path, optionally across drives.")
            .WithDescription(
                "Renames or relocates the target file/directory. Returns the post-move file row " +
                "on 200 OK. Within-drive moves (file or directory) are pure metadata mutations — " +
                "the bytes never relocate. Cross-drive single-file moves copy the bytes onto the " +
                "target drive and delete the source (best-effort). Cross-drive directory moves " +
                "return 400 CrossDriveDirectoryUnsupported in v1.5 — see follow-up issue. Path " +
                "traversal or other malformed TargetPath returns 400 InvalidPath; an existing " +
                "file at the target path or descendant within the target directory prefix " +
                "returns 409 Conflict; files in another drive collapse to 404 (deliberately, to " +
                "prevent cross-drive enumeration).");

        return app;
    }

    private static async Task<IResult> MoveFileAsync(
        Guid driveId,
        Guid fileId,
        MoveFileRequest request,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var command = new MoveFileCommand(driveId, fileId, request.TargetPath, request.TargetDriveId);
        var result = await mediator.Send(command, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return Results.Ok(ToDto(result.Value!));
        }

        return result.ErrorCode switch
        {
            "NotFound" => Results.NotFound(),
            "InvalidPath" => Results.BadRequest(new { code = result.ErrorCode, message = result.ErrorMessage }),
            "Conflict" => Results.Conflict(new { code = result.ErrorCode, message = result.ErrorMessage }),
            "CrossDriveDirectoryUnsupported" => Results.BadRequest(new { code = result.ErrorCode, message = result.ErrorMessage }),
            "ValidationError" => Results.BadRequest(new { code = result.ErrorCode, message = result.ErrorMessage }),
            _ => Results.Problem(statusCode: 500, detail: result.ErrorMessage ?? "Move failed."),
        };
    }

    /// <summary>
    /// Local DTO mapper. Duplicates <see cref="FileListEndpoints"/>'s <c>ToDto</c> shape with the
    /// new <c>DriveId</c> field surfaced — STRG-040 added the field on <see cref="FileItemDto"/>
    /// so the move response can communicate the post-move drive without a second round-trip.
    /// </summary>
    private static FileItemDto ToDto(FileItem f) => new(
        f.Id,
        f.DriveId,
        f.Name,
        f.Path,
        f.Size,
        f.MimeType,
        f.IsDirectory,
        f.ContentHash,
        f.CreatedAt,
        f.UpdatedAt);
}

/// <summary>
/// Request body for the move endpoint. <see cref="TargetDriveId"/> is optional — when omitted,
/// the move is constrained to the source drive (the only mode v1 implements).
/// </summary>
public sealed record MoveFileRequest(string TargetPath, Guid? TargetDriveId);
