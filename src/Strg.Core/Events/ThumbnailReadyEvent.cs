using Strg.Core.Domain;

namespace Strg.Core.Events;

/// <summary>
/// Published by <c>ThumbnailGenerationConsumer</c> after a per-variant blob successfully lands
/// at <see cref="ThumbnailStatus.Ready"/>. Consumed by <c>GraphQlSubscriptionPublisher</c> to
/// push the live <c>thumbnailReady(fileId)</c> subscription payload.
///
/// <para>Outbox-published BEFORE <c>SaveChangesAsync</c> on the row update so the event and
/// the state change commit atomically.</para>
/// </summary>
public sealed record ThumbnailReadyEvent(
    Guid TenantId,
    Guid FileId,
    Guid FileVersionId,
    string Variant,
    string Format,
    int Width,
    int Height) : IDomainEvent;
