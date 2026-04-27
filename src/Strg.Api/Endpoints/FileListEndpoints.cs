using Mediator;
using Strg.Api.Auth;
using Strg.Application.Features.Files.List;
using Strg.Core.Domain;

namespace Strg.Api.Endpoints;

/// <summary>
/// STRG-038 — REST endpoint that lists files in a drive with pagination, optional recursive
/// descent, and a path-prefix filter. The endpoint is a thin protocol shim: query-string
/// binding, page-size capping, dispatch via <see cref="IMediator"/>, and projection from
/// <see cref="FileItem"/> to <see cref="FileItemDto"/>. All filtering and ordering live in
/// <see cref="ListFilesHandler"/>.
///
/// <para><b>Tenant isolation</b> is the global query filter on <c>StrgDbContext</c>'s file and
/// drive sets — the endpoint cannot bypass it. <c>RequireAuthorization(AuthPolicies.FilesRead)</c>
/// stops scope-deficient callers with HTTP 403 before the handler runs. The
/// <see cref="FileItemDto"/> projection deliberately drops <see cref="FileItem.StorageKey"/>,
/// <see cref="TenantedEntity.TenantId"/>, and <see cref="FileItem.ParentId"/> so the wire
/// response cannot leak internal storage credentials, tenant boundaries, or unreliable
/// (sparsely-populated) parent-id information.</para>
/// </summary>
public static class FileListEndpoints
{
    private const int MaxPageSize = 200;
    private const int DefaultPageSize = 50;

    public static IEndpointRouteBuilder MapFileListEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/drives/{driveId:guid}/files", ListFilesAsync)
            .RequireAuthorization(AuthPolicies.FilesRead)
            .WithName("ListFiles")
            .WithTags("Files")
            .WithSummary("List files in a drive with pagination and optional recursive descent.")
            .WithDescription(
                "Returns a paginated list of files and folders under the given drive. " +
                "Use 'path' to scope to a sub-folder (default '/' for the drive root) and " +
                "'recursive=true' to include nested descendants. The 'pageSize' parameter is " +
                "capped server-side at 200 regardless of the client value.");

        return app;
    }

    private static async Task<IResult> ListFilesAsync(
        Guid driveId,
        IMediator mediator,
        CancellationToken cancellationToken,
        string? path = "/",
        bool recursive = false,
        int page = 1,
        int pageSize = DefaultPageSize)
    {
        // Cap pageSize first so a 999-item request returns 200 even on the routing layer's
        // log surface. Math.Clamp guards both upper (200) and lower (1) bounds. The handler
        // re-applies the same clamp as defence-in-depth — see ListFilesHandler.MaxPageSize.
        var cappedPageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var clampedPage = Math.Max(page, 1);

        var query = new ListFilesQuery(
            driveId,
            path ?? string.Empty,
            recursive,
            clampedPage,
            cappedPageSize);

        var result = await mediator.Send(query, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return Results.NotFound();
        }

        var items = result.Items.Select(FileItemDto.From).ToArray();
        return Results.Ok(new FileListResponse(items, result.Page, result.PageSize, result.TotalCount));
    }
}

public record FileItemDto(
    Guid Id,
    string Name,
    string Path,
    long Size,
    string MimeType,
    bool IsDirectory,
    string? ContentHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Projects a <see cref="FileItem"/> onto its wire-safe DTO. Centralised so non-listing
    /// endpoints (folder creation in particular — STRG-042) can reuse the same projection
    /// without re-deriving the field set, which would risk drift on additions like
    /// <c>StorageKey</c> / <c>TenantId</c> / <c>ParentId</c> that are deliberately stripped here.
    /// </summary>
    public static FileItemDto From(FileItem file) => new(
        file.Id,
        file.Name,
        file.Path,
        file.Size,
        file.MimeType,
        file.IsDirectory,
        file.ContentHash,
        file.CreatedAt,
        file.UpdatedAt);
}

public record FileListResponse(
    IReadOnlyList<FileItemDto> Items,
    int Page,
    int PageSize,
    int TotalCount);
