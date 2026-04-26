using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Strg.Core.Domain;
using Strg.Core.Exceptions;
using Strg.Core.Services;
using Strg.Core.Storage;
using tusdotnet.Models.Configuration;

namespace Strg.Infrastructure.Upload;

/// <summary>
/// Factory for the tusdotnet <see cref="Events"/> bundle. Three event hooks coordinate the
/// upload lifecycle:
/// <list type="bullet">
///   <item><c>OnAuthorizeAsync</c> — blocks unauthenticated requests with 401. tusdotnet runs
///     after <c>UseAuthentication</c>, so <c>HttpContext.User.Identity.IsAuthenticated</c> is
///     authoritative.</item>
///   <item><c>OnBeforeCreateAsync</c> — validates the <c>driveId</c> query string parameter,
///     parses the <c>Upload-Metadata</c> header (<c>path</c>, <c>filename</c>, optional
///     <c>contentType</c>), runs <see cref="StoragePath.Parse"/> on the path (path-traversal →
///     422), and runs the pre-quota-check (declared length over remaining quota → 413). All
///     validated parts are stashed in <c>HttpContext.Items</c> so <see cref="StrgTusStore.CreateFileAsync"/>
///     can read them without re-parsing.</item>
///   <item><c>OnFileCompleteAsync</c> — calls <see cref="StrgTusStore.FinalizeAsync"/> and
///     translates <see cref="QuotaExceededException"/> (thrown by
///     <see cref="IQuotaService.CommitAsync"/>) to a 413 response. Other exceptions propagate
///     and tusdotnet emits a 500.</item>
/// </list>
/// </summary>
public static class StrgTusEvents
{
    public static Events Build()
    {
        return new Events
        {
            OnAuthorizeAsync = OnAuthorize,
            OnBeforeCreateAsync = OnBeforeCreate,
            OnFileCompleteAsync = OnFileComplete,
        };
    }

    private static Task OnAuthorize(AuthorizeContext ctx)
    {
        if (ctx.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            ctx.FailRequest(HttpStatusCode.Unauthorized);
            return Task.CompletedTask;
        }
        return Task.CompletedTask;
    }

    private static async Task OnBeforeCreate(BeforeCreateContext ctx)
    {
        var httpContext = ctx.HttpContext;

        // 1) driveId from query string. Per spec: routing concern, separate from metadata.
        var driveIdRaw = httpContext.Request.Query["driveId"].ToString();
        if (string.IsNullOrWhiteSpace(driveIdRaw) || !Guid.TryParse(driveIdRaw, out var driveId))
        {
            ctx.FailRequest("driveId query parameter is required and must be a valid Guid");
            return;
        }

        // 2) Required metadata: path + filename. contentType is optional (defaults to application/octet-stream).
        if (!ctx.Metadata.TryGetValue("path", out var pathMeta) || pathMeta.HasEmptyValue)
        {
            ctx.FailRequest("path metadata is required");
            return;
        }
        if (!ctx.Metadata.TryGetValue("filename", out var filenameMeta) || filenameMeta.HasEmptyValue)
        {
            ctx.FailRequest("filename metadata is required");
            return;
        }

        var rawPath = pathMeta.GetString(System.Text.Encoding.UTF8);
        var filename = filenameMeta.GetString(System.Text.Encoding.UTF8);
        var mimeType = ctx.Metadata.TryGetValue("contentType", out var ctMeta) && !ctMeta.HasEmptyValue
            ? ctMeta.GetString(System.Text.Encoding.UTF8)
            : System.Net.Mime.MediaTypeNames.Application.Octet;

        // 3) Path validation via StoragePath.Parse — TC-004's gate. A failure here MUST emit 422
        // so the client knows the input was rejected as malformed (not "server error").
        string validatedPath;
        try
        {
            validatedPath = StoragePath.Parse(rawPath).Value;
        }
        catch (StoragePathException ex)
        {
            ctx.FailRequest(HttpStatusCode.UnprocessableEntity, ex.Message);
            return;
        }

        // 4) Pre-quota-check (early-rejection optimisation per AC). Note the actual reservation
        // happens at FinalizeAsync (Commit-as-reservation per STRG-032); this Check is advisory
        // and a concurrent upload can still race past it. The Commit at finalize is the
        // authoritative gate.
        var quotaService = httpContext.RequestServices.GetRequiredService<IQuotaService>();
        var currentUser = httpContext.RequestServices.GetRequiredService<ICurrentUser>();
        QuotaCheckResult checkResult;
        try
        {
            checkResult = await quotaService.CheckAsync(currentUser.UserId, ctx.UploadLength, ctx.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (QuotaExceededException)
        {
            // Per IQuotaService class doc, missing-user collapses to QuotaExceededException — the
            // user-facing surface is the same 413 either way (enumeration-oracle-safe).
            ctx.FailRequest(HttpStatusCode.RequestEntityTooLarge,
                $"Upload-Length {ctx.UploadLength} exceeds remaining quota");
            return;
        }
        if (!checkResult.IsAllowed)
        {
            ctx.FailRequest(HttpStatusCode.RequestEntityTooLarge,
                $"Upload-Length {ctx.UploadLength} exceeds remaining quota");
            return;
        }

        // 5) Stash validated parts for CreateFileAsync. HttpContext.Items is per-request, so
        // these don't leak across uploads.
        httpContext.Items[StrgTusStore.ItemKeyDriveId] = driveId;
        httpContext.Items[StrgTusStore.ItemKeyPath] = validatedPath;
        httpContext.Items[StrgTusStore.ItemKeyFilename] = filename;
        httpContext.Items[StrgTusStore.ItemKeyMimeType] = mimeType;
    }

    private static async Task OnFileComplete(FileCompleteContext ctx)
    {
        var store = ctx.HttpContext.RequestServices.GetRequiredService<StrgTusStore>();
        var logger = ctx.HttpContext.RequestServices.GetRequiredService<ILogger<StrgTusStore>>();

        try
        {
            await store.FinalizeAsync(ctx.FileId, ctx.CancellationToken).ConfigureAwait(false);
        }
        catch (QuotaExceededException)
        {
            // The pre-quota-check at OnBeforeCreate is advisory; the authoritative gate is
            // CommitAsync inside FinalizeAsync. Translate to 413 so the client sees the same
            // status as the early-rejection path.
            ctx.HttpContext.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            logger.LogInformation("Upload {FileId} rejected at finalize: quota exceeded", ctx.FileId);
        }
        catch (StoragePathException ex)
        {
            // Defensive — OnBeforeCreate already validated, but a future caller path could reach
            // FinalizeAsync without going through OnBeforeCreate (e.g., a tusdotnet protocol
            // change). 422 is the right surface.
            ctx.HttpContext.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            logger.LogWarning(ex, "Upload {FileId} rejected at finalize: invalid storage path", ctx.FileId);
        }
    }
}
