using Mediator;
using Strg.Api.Auth;
using Strg.Api.Validators;
using Strg.Application.Features.Files.Copy;
using Strg.Core;
using Strg.Core.Domain;
using Strg.Core.Exceptions;

namespace Strg.Api.Endpoints;

/// <summary>
/// STRG-041 — REST endpoint that copies a <see cref="FileItem"/> to a new path, optionally on a
/// different drive. Thin protocol shim: route binding, body deserialization, dispatch via
/// <see cref="IMediator"/>, status-code mapping on the <see cref="Result{T}"/> outcome, and a
/// <see cref="QuotaExceededException"/> catch that surfaces HTTP 507 Insufficient Storage. All
/// business logic — bytes relocation, quota Commit/Release, FileItem/FileVersion/FileKey
/// creation, outbox publish — lives in <see cref="CopyFileHandler"/>.
///
/// <para><b>Tenant isolation</b> is the global query filter on <c>StrgDbContext</c>'s file and
/// drive sets — the endpoint cannot bypass it. <c>RequireAuthorization(AuthPolicies.FilesWrite)</c>
/// stops scope-deficient callers with HTTP 403 before the handler runs. A file whose
/// <c>DriveId</c> does not match the route is collapsed to 404 inside the handler so the wire
/// response cannot be used as an enumeration oracle for files in other drives.</para>
///
/// <para><b>v1.5 deferral.</b> Directory copy returns HTTP 400 <c>DirectoryCopyUnsupported</c> in
/// v1.5 — mirrors STRG-040's <c>CrossDriveDirectoryUnsupported</c> shape. See follow-up issue.</para>
///
/// <para><b>Quota → 507 mapping is local.</b> <see cref="Strg.Core.Services.IQuotaService.CommitAsync"/>
/// throws <see cref="QuotaExceededException"/> on shortfall. There is no global exception filter
/// that maps it to 507 (the TUS path translates to 413 PayloadTooLarge, the WebDAV path to 507
/// per RFC 4918 §9.7.3); for the REST copy endpoint the issue spec mandates 507, so we catch the
/// exception locally rather than retrofitting a global filter (out-of-scope for STRG-041).</para>
/// </summary>
public static class FileCopyEndpoints
{
    public static IEndpointRouteBuilder MapFileCopyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/drives/{driveId:guid}/files/{fileId:guid}/copy", CopyFileAsync)
            .RequireAuthorization(AuthPolicies.FilesWrite)
            .AddEndpointFilter<ValidationProblemDetailsFilter<CopyFileRequest>>()
            .WithName("CopyFile")
            .WithTags("Files")
            .WithSummary("Copy a file to a new path, optionally across drives.")
            .WithDescription(
                "Copies the source file's current head version to a new path. Returns the new " +
                "FileItem on 201 Created with a Location header pointing at the new file. The " +
                "source file is never touched. A new FileVersion (version 1) is always created " +
                "for the copy. Quota is reserved against the caller's user before bytes are " +
                "relocated; insufficient storage returns 507. Path traversal or other malformed " +
                "TargetPath returns 400 InvalidPath; an existing file at the target path returns " +
                "409 Conflict; files in another drive collapse to 404. Directory copy returns 400 " +
                "DirectoryCopyUnsupported in v1.5 — see follow-up issue.");

        return app;
    }

    private static async Task<IResult> CopyFileAsync(
        Guid driveId,
        Guid fileId,
        CopyFileRequest request,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var command = new CopyFileCommand(driveId, fileId, request.TargetPath, request.TargetDriveId);

        Result<FileItem> result;
        try
        {
            result = await mediator.Send(command, cancellationToken).ConfigureAwait(false);
        }
        catch (QuotaExceededException)
        {
            // Per IQuotaService class doc, missing-user collapses into the same exception type as
            // a real shortfall — surface as 507 either way (enumeration-oracle-safe).
            return Results.StatusCode(StatusCodes.Status507InsufficientStorage);
        }

        if (result.IsSuccess)
        {
            var dto = ToDto(result.Value!);
            return Results.Created(
                $"/api/v1/drives/{dto.DriveId}/files/{dto.Id}",
                dto);
        }

        return result.ErrorCode switch
        {
            "NotFound" => Results.NotFound(),
            "InvalidPath" => Results.BadRequest(new { code = result.ErrorCode, message = result.ErrorMessage }),
            "Conflict" => Results.Conflict(new { code = result.ErrorCode, message = result.ErrorMessage }),
            "DirectoryCopyUnsupported" => Results.BadRequest(new { code = result.ErrorCode, message = result.ErrorMessage }),
            "ValidationError" => Results.BadRequest(new { code = result.ErrorCode, message = result.ErrorMessage }),
            _ => Results.Problem(statusCode: 500, detail: result.ErrorMessage ?? "Copy failed."),
        };
    }

    /// <summary>
    /// Local DTO mapper. Mirrors <see cref="FileMoveEndpoints"/>'s <c>ToDto</c> shape — a shared
    /// helper would force a new top-level type for what is currently a one-line projection per
    /// endpoint, and the existing precedent is per-endpoint duplication.
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
/// Request body for the copy endpoint. <see cref="TargetDriveId"/> is optional — when omitted,
/// the copy lands on the source drive.
/// </summary>
public sealed record CopyFileRequest(string TargetPath, Guid? TargetDriveId);
