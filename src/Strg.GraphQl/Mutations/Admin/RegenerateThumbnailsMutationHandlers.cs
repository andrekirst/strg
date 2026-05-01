using HotChocolate.Authorization;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Core.Events;
using Strg.Infrastructure.Data;

namespace Strg.GraphQl.Mutations.Admin;

/// <summary>
/// STRG-342 — admin-triggered backfill mutation. Enumerates <see cref="FileVersion"/> rows
/// without a <c>Ready</c> or <c>Unsupported</c> thumbnail entry, and publishes one
/// <see cref="ThumbnailGenerationRequestedEvent"/> per candidate to the outbox.
///
/// <para><b>Dedicated event, not republished <see cref="FileUploadedEvent"/>.</b> Republishing
/// the upload event would double-write audit rows via <c>AuditLogConsumer</c>. The dedicated
/// event has only the thumbnail consumer subscribing to it.</para>
///
/// <para>Returns immediately after staging events — generation is async. The admin UI polls the
/// affected files (via <c>FileItem.thumbnail</c>) or subscribes to <c>thumbnailReady</c>.</para>
/// </summary>
[ExtendObjectType<AdminMutations>]
public sealed class RegenerateThumbnailsMutationHandlers
{
    [Authorize(Policy = "Admin")]
    public async Task<RegenerateThumbnailsPayload> RegenerateThumbnailsAsync(
        Guid? driveId,
        DateTime? olderThan,
        [Service] StrgDbContext db,
        [Service] IPublishEndpoint bus,
        [Service] ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        // Build the candidate query with optional filters. The composite query inherits the
        // tenant filter on FileItem (TenantedEntity), so cross-tenant driveId values yield
        // empty result sets — no error needed.
        var query =
            from v in db.FileVersions
            join f in db.Files on v.FileId equals f.Id
            select new { v.Id, v.FileId, f.DriveId, v.CreatedAt };

        if (driveId is { } d)
        {
            query = query.Where(x => x.DriveId == d);
        }

        if (olderThan is { } cutoff)
        {
            query = query.Where(x => x.CreatedAt < cutoff);
        }

        // Skip rows that already have a Ready or Unsupported entry across ANY variant — those
        // are settled (Unsupported includes the encrypted-drive carve-out, which we don't want
        // to retry). Failed rows ARE retried (admin pressed the button explicitly).
        query = query.Where(x => !db.ThumbnailEntries.Any(t =>
            t.FileVersionId == x.Id
            && (t.Status == ThumbnailStatus.Ready || t.Status == ThumbnailStatus.Unsupported)));

        var candidates = await query
            .Select(x => new { x.FileId, FileVersionId = x.Id, x.DriveId })
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            await bus.Publish(
                new ThumbnailGenerationRequestedEvent(
                    tenant.TenantId,
                    candidate.FileId,
                    candidate.FileVersionId,
                    candidate.DriveId),
                cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);

        return new RegenerateThumbnailsPayload(candidates.Count);
    }
}

public sealed record RegenerateThumbnailsPayload(int FilesQueued);
