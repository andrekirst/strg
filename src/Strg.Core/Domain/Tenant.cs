namespace Strg.Core.Domain;

public sealed class Tenant : Entity
{
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
