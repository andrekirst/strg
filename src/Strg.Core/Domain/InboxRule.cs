namespace Strg.Core.Domain;

public sealed class InboxRule : TenantedEntity
{
    public Guid DriveId { get; init; }
    public required string Name { get; set; }
    public required string ConditionJson { get; set; }
    public required string ActionJson { get; set; }
    public int Priority { get; set; }
    public bool IsEnabled { get; set; } = true;
}
