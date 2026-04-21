namespace Strg.Core.Domain;

public sealed class Tag : TenantedEntity
{
    private readonly string _key = string.Empty;

    public Guid FileId { get; init; }
    public Guid UserId { get; init; }

    public required string Key
    {
        get => _key;
        init => _key = value.ToLowerInvariant();
    }

    public required string Value { get; set; }
    public TagValueType ValueType { get; set; } = TagValueType.String;
}

public enum TagValueType { String, Number, Boolean }
