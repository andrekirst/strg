using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Strg.Core.Domain;
using Strg.Core.Events;
using Strg.Infrastructure.Data;
using Strg.Plugin.Abstractions.Storage;

namespace Strg.Infrastructure.Messaging.Consumers;

/// <summary>
/// STRG-332 — propagates <see cref="FileDeletedEvent"/> to the thumbnail subsystem. Soft-deletes
/// every <see cref="ThumbnailEntry"/> for the file (across all versions) and best-effort-deletes
/// the corresponding blobs.
///
/// <para><b>Idempotency.</b> Re-delivery finds rows already soft-deleted (excluded by the global
/// query filter) and reads zero — the consumer no-ops. Best-effort blob delete inherits
/// <see cref="IStorageProvider.DeleteAsync"/>'s idempotency contract.</para>
///
/// <para><b>Why soft-delete and not cascade.</b> The cascade on <see cref="FileVersion"/> handles
/// row removal when a version row is physically removed (prune). On a regular file delete the
/// version rows stay (soft-deleted via the file's chain), so we soft-delete thumbnails to mirror.
/// A blob orphan is preferable to a stuck cleanup loop — blobs get reclaimed by the prune-loop
/// extension when the version eventually prunes.</para>
/// </summary>
public sealed class ThumbnailCleanupConsumer(
    StrgDbContext db,
    IStorageProviderRegistry storageRegistry,
    ILogger<ThumbnailCleanupConsumer> logger) : IConsumer<FileDeletedEvent>
{
    public async Task Consume(ConsumeContext<FileDeletedEvent> context)
    {
        var msg = context.Message;

        // Consumer scope has empty ITenantContext (MassTransit dispatches outside HTTP). The
        // global tenant + soft-delete filters on TenantedEntity would otherwise return zero
        // rows. Bypass + re-apply both predicates inline using the event-carried tenantId.
        var thumbnails = await db.ThumbnailEntries
            .IgnoreQueryFilters()
            .Where(t => t.FileId == msg.FileId
                        && t.TenantId == msg.TenantId
                        && !t.DeletedAt.HasValue)
            .ToListAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (thumbnails.Count == 0)
        {
            return;
        }

        // Resolve provider once — every thumbnail lives on the same drive as its source file.
        var drive = await db.Drives
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                d => d.Id == msg.DriveId
                     && d.TenantId == msg.TenantId
                     && !d.DeletedAt.HasValue,
                context.CancellationToken)
            .ConfigureAwait(false);
        if (drive is not null)
        {
            var provider = ResolveProvider(drive);

            foreach (var thumbnail in thumbnails)
            {
                if (thumbnail.Status != ThumbnailStatus.Ready
                    || string.IsNullOrEmpty(thumbnail.StorageKey))
                {
                    continue;
                }

                try
                {
                    var path = StoragePath.Parse(thumbnail.StorageKey);
                    await provider.DeleteAsync(path.Value, context.CancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Best-effort. Failure here MUST NOT block the soft-delete; we'd rather have
                    // an orphan blob than a stuck cleanup. Future prune sweep recovers.
                    logger.LogWarning(ex,
                        "ThumbnailCleanupConsumer: best-effort blob delete failed for thumbnail {ThumbnailId}",
                        thumbnail.Id);
                }
            }
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var thumbnail in thumbnails)
        {
            thumbnail.DeletedAt = now;
        }
        // Re-attach as Modified — the rows were loaded outside the change tracker after
        // IgnoreQueryFilters() returned them; explicit Update() ensures the timestamp write
        // reaches the DB.
        db.ThumbnailEntries.UpdateRange(thumbnails);
        await db.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);
    }

    private IStorageProvider ResolveProvider(Drive drive)
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
        return storageRegistry.Resolve(drive.ProviderType, config);
    }
}
