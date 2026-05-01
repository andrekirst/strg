using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Strg.Api.Auth;
using Strg.Core.Domain;
using Strg.Core.Services;
using Strg.Infrastructure.Data;
using Strg.Plugin.Abstractions.Storage;

namespace Strg.Api.Endpoints;

/// <summary>
/// STRG-339 — REST endpoint for thumbnails. Streams the WebP blob from
/// <see cref="IStorageProvider"/>, sets a strong content-addressed ETag
/// (<c>ContentHash + Variant + Format</c>) with <c>Cache-Control: private, immutable</c>,
/// and returns 304 / 202 / 404 per the status matrix.
///
/// <para><b>Auth.</b> Same <c>FilesRead</c> policy as the download endpoint — readers of the
/// file already have the right to its thumbnail. The fall-through behaviour relies on the
/// global tenant query filter: cross-tenant <c>fileId</c> lookups return null and produce 404.</para>
///
/// <para><b>Cache.</b> Cache-Control is <c>private</c> (browser cache OK, shared cache NO) so a
/// CDN cannot accidentally serve the same path across tenants if a fileId GUID ever collided.
/// <c>immutable</c> + <c>max-age=31536000</c> tells the browser to never revalidate within the
/// year — safe because the ETag changes whenever <c>FileVersion.ContentHash</c> changes (new
/// version) and old versions retain their old key indefinitely.</para>
/// </summary>
public static class ThumbnailEndpoints
{
    public const string EndpointName = "GetFileThumbnail";

    private const string CacheControlValue = "private, max-age=31536000, immutable";
    private const string ContentTypeWebP = "image/webp";

    public static IEndpointRouteBuilder MapThumbnailEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/drives/{driveId:guid}/files/{fileId:guid}/thumbnail", GetAsync)
            .RequireAuthorization(AuthPolicies.FilesRead)
            .WithName(EndpointName);

        return app;
    }

    private static async Task<IResult> GetAsync(
        HttpContext ctx,
        Guid driveId,
        Guid fileId,
        string variant,
        StrgDbContext db,
        IThumbnailRepository thumbnails,
        IDriveRepository driveRepo,
        IStorageProviderRegistry storageRegistry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(variant) || !ThumbnailVariants.IsKnown(variant))
        {
            return Results.BadRequest(new { error = "unknown-variant", variant });
        }

        // Resolve via the file repository's tenant filter — a cross-tenant fileId returns null
        // and we report 404 without leaking existence.
        var file = await db.Files
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == fileId && f.DriveId == driveId, cancellationToken)
            .ConfigureAwait(false);
        if (file is null)
        {
            return Results.NotFound();
        }

        // Latest version. The thumbnail row is keyed on the FileVersionId so we always serve
        // the version's thumbnail for the latest file content.
        var version = await db.FileVersions
            .AsNoTracking()
            .Where(v => v.FileId == fileId)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (version is null)
        {
            return Results.NotFound();
        }

        var entry = await thumbnails.GetAsync(version.Id, variant, ThumbnailFormats.WebP, cancellationToken)
            .ConfigureAwait(false);

        if (entry is null)
        {
            // Consumer hasn't fired yet — common right after upload.
            ctx.Response.Headers["Retry-After"] = "5";
            return Results.StatusCode(StatusCodes.Status202Accepted);
        }

        return entry.Status switch
        {
            ThumbnailStatus.Pending => AcceptedWithRetry(ctx),
            ThumbnailStatus.Failed or ThumbnailStatus.Unsupported => Results.NotFound(),
            ThumbnailStatus.Ready => await StreamReadyAsync(
                ctx, entry, version, driveRepo, storageRegistry, cancellationToken).ConfigureAwait(false),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    private static IResult AcceptedWithRetry(HttpContext ctx)
    {
        ctx.Response.Headers["Retry-After"] = "5";
        return Results.StatusCode(StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> StreamReadyAsync(
        HttpContext ctx,
        ThumbnailEntry entry,
        FileVersion version,
        IDriveRepository driveRepo,
        IStorageProviderRegistry storageRegistry,
        CancellationToken cancellationToken)
    {
        // Strong ETag is content-addressed: same file content → same ContentHash → same ETag.
        // Quoted per RFC 7232 §2.3.
        var etag = $"\"{version.ContentHash}-{entry.Variant}-{entry.Format}\"";

        // If-None-Match exact match → 304. Stream is not opened.
        if (ctx.Request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var ifNoneMatch)
            && ifNoneMatch.Any(v => string.Equals(v, etag, StringComparison.Ordinal)))
        {
            ctx.Response.Headers[HeaderNames.ETag] = etag;
            ctx.Response.Headers[HeaderNames.CacheControl] = CacheControlValue;
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        // Drive comes from the route — every thumbnail lives on the same drive as its source.
        var driveId = ctx.Request.RouteValues["driveId"] is Guid did ? did : Guid.Empty;
        var drive = await driveRepo.GetByIdAsync(driveId, cancellationToken).ConfigureAwait(false);
        if (drive is null)
        {
            return Results.NotFound();
        }

        var provider = ResolveProvider(drive, storageRegistry);
        var path = StoragePath.Parse(entry.StorageKey);
        var stream = await provider.ReadAsync(path.Value, 0, cancellationToken).ConfigureAwait(false);

        ctx.Response.Headers[HeaderNames.ETag] = etag;
        ctx.Response.Headers[HeaderNames.CacheControl] = CacheControlValue;
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";

        var contentType = entry.Format == ThumbnailFormats.Jpeg
            ? "image/jpeg"
            : ContentTypeWebP;

        return Results.File(stream, contentType: contentType, enableRangeProcessing: false);
    }

    private static IStorageProvider ResolveProvider(Drive drive, IStorageProviderRegistry registry)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var json = System.Text.Json.JsonDocument.Parse(drive.ProviderConfig);
        foreach (var property in json.RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => property.Value.GetString(),
                System.Text.Json.JsonValueKind.Null => null,
                _ => property.Value.GetRawText(),
            };
        }
        var config = new DictionaryStorageProviderConfig(values);
        return registry.Resolve(drive.ProviderType, config);
    }
}
