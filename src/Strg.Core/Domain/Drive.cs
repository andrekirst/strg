namespace Strg.Core.Domain;

public sealed class Drive : TenantedEntity
{
    public required string Name { get; set; }
    public required string ProviderType { get; set; }
    public string ProviderConfig { get; set; } = "{}";
    public string VersioningPolicy { get; set; } = """{"mode":"none"}""";
    public bool EncryptionEnabled { get; set; }
    public bool IsDefault { get; set; }
}
