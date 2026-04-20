namespace Strg.Core.Domain;

public abstract class TenantedEntity : Entity
{
    public Guid TenantId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }

    public bool IsDeleted => DeletedAt.HasValue;
}
