using System.Buffers;
using System.Text.Encodings.Web;
using Mediator;
using Microsoft.Net.Http.Headers;
using Strg.Api.Auth;
using Strg.Application.Features.Files.Download;

namespace Strg.Api.Endpoints;

/// <summary>
/// STRG-037 — HTTP shim for the file-download flow. The endpoint owns only the protocol
/// concerns: parsing the <c>Range</c> header into a domain DTO, dispatching the
/// <see cref="DownloadFileCommand"/> via Mediator, mapping <see cref="Strg.Core.Result{T}"/>
/// failure codes to HTTP status, and streaming the result's <see cref="FileDownloadResult.Content"/>
/// to <see cref="HttpResponse.Body"/>. All business logic — drive/file resolution, encryption
/// branching, range satisfiability, audit emission — lives in <see cref="DownloadFileHandler"/>.
///
/// <para><b>Tenant isolation, encryption, audit emission</b> are properties of the handler /
/// pipeline (see <c>TenantScopeBehavior</c>, <c>AuditBehavior</c>); the endpoint cannot
/// bypass them. <c>RequireAuthorization(AuthPolicies.FilesRead)</c> stops scope-deficient
/// callers with HTTP 403 before the handler runs.</para>
/// </summary>
public static class FileDownloadEndpoints
{
    private const int CopyBufferSize = 81920;
    private const string AcceptRangesBytes = "bytes";

    public static IEndpointRouteBuilder MapFileDownloadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/drives/{driveId:guid}/files/{fileId:guid}/content", DownloadAsync)
            .RequireAuthorization(AuthPolicies.FilesRead)
            .WithName("DownloadFileContent");

        return app;
    }

    private static async Task DownloadAsync(
        HttpContext ctx,
        Guid driveId,
        Guid fileId,
        IMediator mediator,
        ILogger<FileDownloadLog> logger)
    {
        var cancellationToken = ctx.RequestAborted;
        var range = ParseRange(ctx.Request.GetTypedHeaders().Range);

        var result = await mediator
            .Send(new DownloadFileCommand(driveId, fileId, range), cancellationToken)
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
        // Multi-range requests (multipart/byteranges) and unparseable Range headers are
        // collapsed to "no range" — RFC 7233 permits the server to ignore Range and serve the
        // full representation. A single-range request is the predominant client shape.
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
        // RFC 6266: emit BOTH the legacy quoted-string form (ASCII-only, double-quote / backslash
        // escaped) AND the filename* form (UTF-8 percent-encoded) so clients on either dialect
        // surface the right name. attachment is hardcoded — never inline; never derived from
        // user-controlled data.
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
                // Typed file size on the failure case — no string round-trip. RFC 7233 §4.4
                // requires a Content-Range header on 416 so range-savvy clients can correct
                // their request.
                ctx.Response.Headers[HeaderNames.ContentRange] = $"bytes */{rns.Size}";
                await Results.Problem(
                        statusCode: StatusCodes.Status416RangeNotSatisfiable,
                        detail: "Requested range cannot be satisfied.")
                    .ExecuteAsync(ctx).ConfigureAwait(false);
                return;

            case DownloadFailure.InternalState st:
                logger.LogError("File download internal-state inconsistency: {Detail}", st.Detail);
                await Results.Problem(
                        statusCode: StatusCodes.Status500InternalServerError,
                        detail: "File download failed due to internal state inconsistency.")
                    .ExecuteAsync(ctx).ConfigureAwait(false);
                return;

            default:
                // Compiler-exhaustive over sealed cases today; keep a defensive fallback so a
                // future DownloadFailure subtype added without endpoint update produces a 500
                // with a log entry rather than a silent unhandled response.
                logger.LogError("Unhandled DownloadFailure subtype: {Type}", failure.GetType().Name);
                await Results.Problem(
                        statusCode: StatusCodes.Status500InternalServerError,
                        detail: "Unexpected error.")
                    .ExecuteAsync(ctx).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// Logger category marker — gives the static endpoint method a stable
    /// <c>ILogger&lt;FileDownloadLog&gt;</c> binding without exposing the static class as a
    /// generic type parameter.
    /// </summary>
    public sealed class FileDownloadLog;
}
