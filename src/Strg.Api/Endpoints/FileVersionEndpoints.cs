using System.Buffers;
using System.Text.Encodings.Web;
using Mediator;
using Microsoft.Net.Http.Headers;
using Strg.Api.Auth;
using Strg.Application.Features.Files.Download;
using Strg.Application.Features.Files.DownloadVersion;
using Strg.Application.Features.Files.ListVersions;

namespace Strg.Api.Endpoints;

/// <summary>
/// STRG-044 — HTTP shim for the file-versions read flow. Two endpoints:
/// <list type="bullet">
///   <item><description><c>GET .../versions</c> dispatches <see cref="ListFileVersionsQuery"/>
///   and projects the application-layer view to <see cref="FileVersionDto"/>. Storage keys
///   are NOT exposed.</description></item>
///   <item><description><c>GET .../versions/{versionNumber}/content</c> dispatches
///   <see cref="DownloadFileVersionCommand"/>, mirroring <c>FileDownloadEndpoints</c>'s manual
///   range / 206 / 416 handling so the version-download surface stays aligned with the
///   STRG-037 current-content surface (same Range parsing, same Content-Range emission, same
///   <see cref="DownloadFailure"/> → HTTP mapping).</description></item>
/// </list>
///
/// <para>Tenant isolation, encryption branching, and audit emission are handler / pipeline
/// concerns — the endpoint cannot bypass them. <c>RequireAuthorization(AuthPolicies.FilesRead)</c>
/// stops scope-deficient callers with HTTP 403 before the handler runs.</para>
/// </summary>
public static class FileVersionEndpoints
{
    private const int CopyBufferSize = 81920;
    private const string AcceptRangesBytes = "bytes";

    public static IEndpointRouteBuilder MapFileVersionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/drives/{driveId:guid}/files/{fileId:guid}/versions", ListAsync)
            .RequireAuthorization(AuthPolicies.FilesRead)
            .WithName("ListFileVersions")
            .WithTags("Files")
            .WithSummary("List all versions of a file, latest first.");

        app.MapGet("/api/v1/drives/{driveId:guid}/files/{fileId:guid}/versions/{versionNumber:int}/content", DownloadAsync)
            .RequireAuthorization(AuthPolicies.FilesRead)
            .WithName("DownloadFileVersionContent")
            .WithTags("Files")
            .WithSummary("Download the content of a specific historical version.");

        return app;
    }

    private static async Task<IResult> ListAsync(
        Guid driveId,
        Guid fileId,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var versions = await mediator
            .Send(new ListFileVersionsQuery(driveId, fileId), cancellationToken)
            .ConfigureAwait(false);

        if (versions is null)
        {
            return Results.NotFound();
        }

        var dtos = new List<FileVersionDto>(versions.Count);
        foreach (var version in versions)
        {
            dtos.Add(new FileVersionDto(
                version.VersionNumber,
                version.Size,
                version.ContentHash,
                version.CreatedAt,
                version.CreatedBy));
        }

        return Results.Ok(dtos);
    }

    private static async Task DownloadAsync(
        HttpContext ctx,
        Guid driveId,
        Guid fileId,
        int versionNumber,
        IMediator mediator,
        ILogger<FileVersionDownloadLog> logger)
    {
        var cancellationToken = ctx.RequestAborted;
        var range = ParseRange(ctx.Request.GetTypedHeaders().Range);

        var result = await mediator
            .Send(new DownloadFileVersionCommand(driveId, fileId, versionNumber, range), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            await WriteFailureAsync(ctx, result.Error!, logger).ConfigureAwait(false);
            return;
        }

        await using var download = result.Value!;
        SetCommonHeaders(ctx.Response, download);

        if (download.IsPartial)
        {
            ctx.Response.StatusCode = StatusCodes.Status206PartialContent;
            ctx.Response.Headers[HeaderNames.ContentRange] =
                $"bytes {download.PartialStart}-{download.PartialEnd}/{download.Size}";
            ctx.Response.Headers.ContentLength = download.ResponseLength;
        }
        else
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.Headers.ContentLength = download.Size;
        }

        await ctx.Response.StartAsync(cancellationToken).ConfigureAwait(false);
        await CopyBoundedAsync(
            download.Content,
            ctx.Response.Body,
            download.IsPartial ? download.ResponseLength : long.MaxValue,
            cancellationToken).ConfigureAwait(false);
    }

    private static DownloadRange? ParseRange(RangeHeaderValue? header)
    {
        if (header is null || header.Ranges.Count != 1)
        {
            return null;
        }

        var item = header.Ranges.First();
        if (!item.From.HasValue && !item.To.HasValue)
        {
            return null;
        }

        return new DownloadRange(item.From, item.To);
    }

    private static void SetCommonHeaders(HttpResponse response, FileDownloadResult download)
    {
        response.Headers[HeaderNames.ContentType] = download.MimeType;
        response.Headers[HeaderNames.ContentDisposition] = BuildContentDisposition(download.Filename);
        response.Headers[HeaderNames.AcceptRanges] = AcceptRangesBytes;
    }

    private static string BuildContentDisposition(string filename)
    {
        var quoted = filename
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        var encoded = UrlEncoder.Default.Encode(filename);
        return $"attachment; filename=\"{quoted}\"; filename*=UTF-8''{encoded}";
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long byteCount, CancellationToken cancellationToken)
    {
        if (byteCount == long.MaxValue)
        {
            await source.CopyToAsync(destination, CopyBufferSize, cancellationToken).ConfigureAwait(false);
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            var remaining = byteCount;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(remaining, buffer.Length);
                var read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WriteFailureAsync(
        HttpContext ctx,
        DownloadFailure failure,
        ILogger logger)
    {
        switch (failure)
        {
            case DownloadFailure.NotFound nf:
                await Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: nf.Detail)
                    .ExecuteAsync(ctx).ConfigureAwait(false);
                return;

            case DownloadFailure.IsDirectory dir:
                await Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: dir.Detail)
                    .ExecuteAsync(ctx).ConfigureAwait(false);
                return;

            case DownloadFailure.RangeNotSatisfiable rns:
                ctx.Response.Headers[HeaderNames.ContentRange] = $"bytes */{rns.Size}";
                await Results.Problem(
                        statusCode: StatusCodes.Status416RangeNotSatisfiable,
                        detail: "Requested range cannot be satisfied.")
                    .ExecuteAsync(ctx).ConfigureAwait(false);
                return;

            case DownloadFailure.InternalState st:
                logger.LogError("File version download internal-state inconsistency: {Detail}", st.Detail);
                await Results.Problem(
                        statusCode: StatusCodes.Status500InternalServerError,
                        detail: "File version download failed due to internal state inconsistency.")
                    .ExecuteAsync(ctx).ConfigureAwait(false);
                return;

            default:
                logger.LogError("Unhandled DownloadFailure subtype: {Type}", failure.GetType().Name);
                await Results.Problem(
                        statusCode: StatusCodes.Status500InternalServerError,
                        detail: "Unexpected error.")
                    .ExecuteAsync(ctx).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// Logger category marker — gives the static endpoint methods a stable
    /// <c>ILogger&lt;FileVersionDownloadLog&gt;</c> binding without exposing the static class
    /// as a generic type parameter. Mirrors <c>FileDownloadEndpoints.FileDownloadLog</c>.
    /// </summary>
    public sealed class FileVersionDownloadLog;
}

/// <summary>
/// Wire shape for STRG-044's list endpoint. Property order matches the JSON output. Excludes
/// <c>StorageKey</c> deliberately — Security Review checklist item: storage keys must not leak
/// to API consumers.
/// </summary>
public record FileVersionDto(
    int VersionNumber,
    long Size,
    string ContentHash,
    DateTimeOffset CreatedAt,
    Guid CreatedBy);
