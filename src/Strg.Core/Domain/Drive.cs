namespace Strg.Core.Domain;

public sealed class Drive : TenantedEntity
{
    public required string Name { get; set; }
    public required string ProviderType { get; set; }
    public string ProviderConfig { get; set; } = "{}";
    public string VersioningPolicy { get; set; } = """{"mode":"none"}""";
    // init-only so a drive's encryption posture cannot flip after creation. Flipping false→true
    // on a drive that already holds plaintext files would lie about which files are protected;
    // flipping true→false would make existing ciphertext undecryptable because the read path
    // skips the envelope parser. v0.2's per-file encryption flag is the path forward for drives
    // that need mixed content; v0.1 keeps the invariant drive-wide.
    public bool EncryptionEnabled { get; init; }
    public bool IsDefault { get; set; }
}
