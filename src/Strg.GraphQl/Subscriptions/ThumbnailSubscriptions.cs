using HotChocolate.Authorization;
using HotChocolate.Execution;
using HotChocolate.Subscriptions;
using Microsoft.AspNetCore.Routing;
using Strg.Core.Events;
using Strg.GraphQl.Types;

namespace Strg.GraphQl.Subscriptions;

/// <summary>
/// Live <c>thumbnailReady(fileId)</c> subscription — fires after
/// <c>ThumbnailGenerationConsumer</c> commits a Ready row for any of the file's variants.
///
/// <para>Authorization mirrors <see cref="FileSubscriptions"/>: the
/// <see cref="AuthorizeAttribute"/> is the subscribe-time gate; the per-(tenant, file) topic
/// key (<see cref="Topics.ThumbnailReady"/>) makes cross-tenant subscriptions structurally empty;
/// the resolver-side guard re-checks tenant on every payload as defence-in-depth.</para>
/// </summary>
[ExtendObjectType("Subscription")]
public sealed class ThumbnailSubscriptions
{
    [Subscribe(With = nameof(SubscribeToThumbnailReadyAsync))]
    [Authorize(Policy = "FilesRead")]
    public Thumbnail ThumbnailReady(
        Guid fileId,
        [EventMessage] ThumbnailReadyEvent evt,
        [GlobalState("tenantId")] Guid tenantId,
        [Service] LinkGenerator linkGenerator)
    {
        // Defence-in-depth: the topic key is keyed on (tenantId, fileId) so cross-tenant events
        // should not reach this resolver at all. The guard pins the invariant against any future
        // regression in the topic-routing layer.
        if (evt.TenantId != tenantId)
        {
            throw new UnauthorizedAccessException("Subscription event tenant mismatch.");
        }
        if (evt.FileId != fileId)
        {
            throw new UnauthorizedAccessException("Subscription event file mismatch.");
        }

        // The event payload doesn't carry DriveId for URL building; subscribers re-query the
        // FileItem.thumbnail GraphQL field for full metadata. Returning a minimal payload keeps
        // the wire format flat and cheap.
        var url = linkGenerator.GetPathByName(
            "GetFileThumbnail",
            new { fileId = evt.FileId })
            ?? $"/api/v1/drives/_/files/{evt.FileId}/thumbnail";
        var fullUrl = $"{url}?variant={Uri.EscapeDataString(evt.Variant)}";

        return new Thumbnail(
            Url: fullUrl,
            Width: evt.Width,
            Height: evt.Height,
            SizeBytes: null,                // not in event; client can re-fetch via DataLoader
            Status: ThumbnailStatusGraphQl.Ready,
            Format: evt.Format,
            ErrorReason: null);
    }

    public ValueTask<ISourceStream<ThumbnailReadyEvent>> SubscribeToThumbnailReadyAsync(
        Guid fileId,
        [GlobalState("tenantId")] Guid tenantId,
        [Service] ITopicEventReceiver receiver,
        CancellationToken cancellationToken) =>
        receiver.SubscribeAsync<ThumbnailReadyEvent>(
            Topics.ThumbnailReady(tenantId, fileId), cancellationToken);
}
