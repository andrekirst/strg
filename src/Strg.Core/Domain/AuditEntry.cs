namespace Strg.Core.Domain;

public sealed class AuditEntry : TenantedEntity
{
    public Guid UserId { get; init; }
    public required string Action { get; init; }
    public required string ResourceType { get; init; }
    public Guid? ResourceId { get; init; }
    public string? Details { get; init; }
    public DateTimeOffset PerformedAt { get; init; } = DateTimeOffset.UtcNow;
}
