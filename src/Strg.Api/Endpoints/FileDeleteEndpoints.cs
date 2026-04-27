using Mediator;
using Strg.Api.Auth;
using Strg.Application.Features.Files.Delete;

namespace Strg.Api.Endpoints;

/// <summary>
/// STRG-039 — REST endpoint that soft-deletes a <c>FileItem</c>. The endpoint is a thin
/// protocol shim: route binding, dispatch via <see cref="IMediator"/>, and a 204/404
/// mapping on the <see cref="Strg.Core.Result"/> outcome. All business logic — drive/file
/// resolution, recursive descent for directories, outbox publish, audit emission via
/// consumer — lives in <see cref="DeleteFileHandler"/>.
///
/// <para><b>Tenant isolation</b> is the global query filter on <c>StrgDbContext</c>'s file
/// set — the endpoint cannot bypass it. <c>RequireAuthorization(AuthPolicies.FilesWrite)</c>
/// stops scope-deficient callers with HTTP 403 before the handler runs. A file whose
/// <c>DriveId</c> does not match the route is collapsed to 404 inside the handler so the
/// wire response cannot be used as an enumeration oracle for files in other drives.</para>
/// </summary>
public static class FileDeleteEndpoints
{
    public static IEndpointRouteBuilder MapFileDeleteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/api/v1/drives/{driveId:guid}/files/{fileId:guid}", DeleteFileAsync)
            .RequireAuthorization(AuthPolicies.FilesWrite)
            .WithName("DeleteFile")
            .WithTags("Files")
            .WithSummary("Soft-delete a file or directory.")
            .WithDescription(
                "Marks the target file (or directory and every descendant under its path " +
                "prefix) as deleted. Physical storage is retained — a separate background " +
                "job will release blobs in a future release. Already-deleted files return " +
                "404. Files belonging to a drive other than the one in the route also " +
                "return 404 (deliberately collapsed, not 403, to prevent cross-drive " +
                "enumeration).");

        return app;
    }

    private static async Task<IResult> DeleteFileAsync(
        Guid driveId,
        Guid fileId,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator
            .Send(new DeleteFileCommand(driveId, fileId), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? Results.NoContent() : Results.NotFound();
    }
}
