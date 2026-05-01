using Mediator;
using Strg.Api.Auth;
using Strg.Api.Validators;
using Strg.Application.Features.Folders.Create;
using Strg.Core;
using Strg.Core.Domain;

namespace Strg.Api.Endpoints;

/// <summary>
/// STRG-042 — REST endpoint that creates a directory <see cref="FileItem"/> in the database. No
/// physical directory hits the storage backend — strg uses virtual paths. The endpoint is a thin
/// protocol shim: route binding, body deserialization, dispatch via <see cref="IMediator"/>, and
/// status-code mapping on the <see cref="Result{T}"/> outcome. All business logic — path parsing,
/// drive-existence check, parent-segment auto-create with ParentId chain, idempotent re-entry,
/// file-collision detection, audit emission — lives in <see cref="CreateFolderHandler"/>.
///
/// <para><b>Tenant isolation</b> is the global query filter on <c>StrgDbContext</c>'s file and
/// drive sets — the endpoint cannot bypass it.
/// <c>RequireAuthorization(AuthPolicies.FilesWrite)</c> stops scope-deficient callers with HTTP
/// 403 before the handler runs.</para>
///
/// <para><b>200 OK on every success, including idempotent re-entry.</b> Unlike the file copy
/// endpoint (201 Created with Location header on a fresh row), folder creation always returns
/// 200 because the call is idempotent — POSTing the same path twice MUST return the same
/// existing row, so a single fixed status code is more honest than a 201/200 split that would
/// require the caller to inspect Location to tell them apart. The acceptance criteria in the
/// issue body specify 200 for both fresh and existing-folder cases.</para>
///
/// <para><b>Naming follows the <c>File&lt;Verb&gt;Endpoints</c> precedent</b>
/// (<c>FileMoveEndpoints</c>, <c>FileCopyEndpoints</c>, <c>FileDeleteEndpoints</c>) so a future
/// <c>FolderDeleteEndpoints</c> / <c>FolderMoveEndpoints</c> fits symmetrically. The
/// bare-aggregate name (<c>DriveEndpoints</c>) is reserved by precedent for files that house
/// many verbs, which this one does not.</para>
/// </summary>
public static class FolderCreateEndpoints
{
    public static IEndpointRouteBuilder MapFolderCreateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/drives/{driveId:guid}/folders", CreateFolderAsync)
            .RequireAuthorization(AuthPolicies.FilesWrite)
            .AddEndpointFilter<ValidationProblemDetailsFilter<CreateFolderRequest>>()
            .WithName("CreateFolder")
            .WithTags("Files")
            .WithSummary("Create a directory FileItem at a virtual path; auto-creates missing parent segments.")
            .WithDescription(
                "Creates a directory row at the supplied path. Parent segments that don't exist " +
                "yet are auto-created in order, with ParentId chained to the previous segment, " +
                "so a single call materializes the entire path. The call is idempotent: re-POSTing " +
                "the same path returns the existing folder row with no duplicate. A path segment " +
                "that already exists as a non-directory FILE returns 409 Conflict. Empty path or " +
                "path containing '..' is rejected by the request-body validator with 400 and an " +
                "RFC 7807 ValidationProblemDetails envelope. Other malformed input the validator " +
                "doesn't catch (reserved names, null bytes) is rejected by the handler with 400 " +
                "and a {code:'InvalidPath',message} envelope. A missing target drive returns 404. " +
                "No physical directory is created in the storage backend — strg uses virtual " +
                "paths and a flat blob store.");

        return app;
    }

    private static async Task<IResult> CreateFolderAsync(
        Guid driveId,
        CreateFolderRequest request,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var command = new CreateFolderCommand(driveId, request.Path);
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
            "ValidationError" => Results.BadRequest(new { code = result.ErrorCode, message = result.ErrorMessage }),
            _ => Results.Problem(statusCode: 500, detail: result.ErrorMessage ?? "Folder creation failed."),
        };
    }

    /// <summary>
    /// Local DTO mapper. Mirrors the projection used by <c>FileMoveEndpoints</c> /
    /// <c>FileCopyEndpoints</c> — a shared helper would force a new top-level type for what is
    /// currently a one-line projection per endpoint, and the existing precedent is per-endpoint
    /// duplication.
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
/// Request body for the folder-creation endpoint. The path is the virtual location of the leaf
/// directory; missing parent segments are auto-created.
/// </summary>
public sealed record CreateFolderRequest(string Path);
