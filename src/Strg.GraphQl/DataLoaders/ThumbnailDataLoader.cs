using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Core.Services;
using Strg.GraphQl.Types;
using Strg.Infrastructure.Data;

namespace Strg.GraphQl.DataLoaders;

/// <summary>
/// Composite key for the thumbnail DataLoader — every <c>FileItem.thumbnail(variant: …)</c>
/// query in a single GraphQL request batches into one SQL fetch keyed on
/// <see cref="FileId"/> + <see cref="Variant"/>.
/// </summary>
public readonly record struct ThumbnailKey(Guid FileId, string Variant);

/// <summary>
/// Batches thumbnail lookups across a single GraphQL request. Without this, a 50-row grid view
/// querying <c>thumbnail(variant: SMALL)</c> on each row would issue 50 SQL round-trips against
/// <c>ThumbnailEntries</c>. The DataLoader collapses to one query.
///
/// <para><b>Latest-version policy.</b> A thumbnail row is keyed on FileVersionId, but consumers
/// of the GraphQL field always want the LATEST version's thumbnail. The batch query joins
/// <c>FileVersions</c> and orders by <c>VersionNumber DESC</c>, picking the first hit per
/// (FileId, Variant) pair.</para>
/// </summary>
public sealed class ThumbnailDataLoader(
    IDbContextFactory<StrgDbContext> dbFactory,
    IBatchScheduler batchScheduler,
    LinkGenerator linkGenerator,
    DataLoaderOptions? options = null)
    : BatchDataLoader<ThumbnailKey, Thumbnail>(batchScheduler, options ?? new DataLoaderOptions())
{
    protected override async Task<IReadOnlyDictionary<ThumbnailKey, Thumbnail>> LoadBatchAsync(
        IReadOnlyList<ThumbnailKey> keys,
        CancellationToken cancellationToken)
    {
        var fileIds = keys.Select(k => k.FileId).Distinct().ToList();
        var variants = keys.Select(k => k.Variant).Distinct().ToList();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Pull every candidate row + its FileVersion's VersionNumber so we can pick the latest.
        // Format is fixed at "webp" in v1 — when JPEG fallback ships, this becomes a per-key
        // filter ranked by client UA preference (out of v1 scope).
        var rows = await (
                from t in db.ThumbnailEntries
                join v in db.FileVersions on t.FileVersionId equals v.Id
                where fileIds.Contains(v.FileId)
                      && variants.Contains(t.Variant)
                      && t.Format == ThumbnailFormats.WebP
                select new { v.FileId, v.VersionNumber, Entry = t, DriveId = v.FileId })
            .ToListAsync(cancellationToken);

        // For each (FileId, Variant), pick the row tied to the highest VersionNumber.
        var picked = rows
            .GroupBy(r => new ThumbnailKey(r.FileId, r.Entry.Variant))
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.VersionNumber).First().Entry);

        // Need the file's DriveId to build the REST URL. Single round-trip; bounded by fileIds.
        var driveIdByFile = await db.Files
            .Where(f => fileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.DriveId })
            .ToDictionaryAsync(x => x.Id, x => x.DriveId, cancellationToken);

        return picked.ToDictionary(
            kv => kv.Key,
            kv => MapToGraphQl(
                kv.Value,
                driveIdByFile.TryGetValue(kv.Key.FileId, out var driveId) ? driveId : Guid.Empty));
    }

    private Thumbnail MapToGraphQl(ThumbnailEntry entry, Guid driveId)
    {
        var url = linkGenerator.GetPathByName(
            "GetFileThumbnail",
            new { driveId, fileId = entry.FileId })
            ?? $"/api/v1/drives/{driveId}/files/{entry.FileId}/thumbnail";

        // Append the variant query string. LinkGenerator omits query args by design.
        var fullUrl = $"{url}?variant={Uri.EscapeDataString(entry.Variant)}";

        var ready = entry.Status == ThumbnailStatus.Ready;
        return new Thumbnail(
            Url: fullUrl,
            Width: ready ? entry.Width : null,
            Height: ready ? entry.Height : null,
            SizeBytes: ready ? entry.SizeBytes : null,
            Status: ThumbnailStatusMap.FromDomain(entry.Status),
            Format: ready ? entry.Format : null,
            ErrorReason: entry.ErrorReason);
    }
}
