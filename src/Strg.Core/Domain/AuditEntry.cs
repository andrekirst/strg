namespace Strg.Core.Domain;

public sealed class AuditEntry : TenantedEntity
{
    public Guid UserId { get; init; }
    public required string Action { get; init; }
    public required string ResourceType { get; init; }
    public Guid? ResourceId { get; init; }
    public string? Details { get; init; }
    public DateTimeOffset PerformedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Idempotency key for audit entries written in response to a MassTransit domain event —
    /// carries the outbox <c>MessageId</c> so at-least-once redelivery collapses to one row.
    /// Nullable because pre-outbox writers (auth, tag, prune) predate the Tranche-5 consumer path
    /// and have no message identity; a partial unique index scopes the constraint to rows where
    /// this is set.
    /// </summary>
    public Guid? EventId { get; init; }
}
