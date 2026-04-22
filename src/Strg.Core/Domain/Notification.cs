namespace Strg.Core.Domain;

/// <summary>
/// Per-user notification row (STRG-064). Currently only quota warnings write here; future
/// share invitations, inbox-rule failures, and backup completions will reuse the same surface
/// so the client can render a unified notification centre.
///
/// <para><b>Payload shape:</b> <see cref="Type"/> carries the discriminator (e.g.
/// <c>"quota.warning"</c>) and <see cref="PayloadJson"/> carries the per-type structured body.
/// Keeping the shape string/JSON rather than a polymorphic entity graph avoids a migration
/// every time a new notification kind lands — the discriminator lives in code, the storage
/// stays flat.</para>
///
/// <para><b>Idempotency:</b> <see cref="EventId"/> mirrors the MassTransit message ID so
/// at-least-once redelivery collapses to one row via the partial unique index on the column.
/// Legacy writers (pre-outbox, manual admin annotations) leave this null — the filter
/// <c>"EventId" IS NOT NULL</c> excludes those rows from the unique scope.</para>
/// </summary>
public sealed class Notification : TenantedEntity
{
    public Guid UserId { get; init; }
    public required string Type { get; init; }
    public required string PayloadJson { get; init; }
    public DateTimeOffset? ReadAt { get; set; }

    /// <summary>
    /// Idempotency key carrying the outbox <c>MessageId</c> from the originating
    /// <c>IDomainEvent</c>. A partial unique index scopes the constraint to rows where this is
    /// set, so legacy EventId-null writers do not collide with each other.
    /// </summary>
    public Guid? EventId { get; init; }
}
