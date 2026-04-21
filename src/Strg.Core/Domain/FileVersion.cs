namespace Strg.Core.Domain;

public sealed class FileVersion : Entity
{
    public Guid FileId { get; init; }
    public int VersionNumber { get; init; }

    /// <summary>
    /// Plaintext byte count — the quota-relevant size. Equals what the user would see if they
    /// downloaded and sized the content. For encrypted drives this is the pre-encryption length.
    /// </summary>
    public long Size { get; init; }

    /// <summary>
    /// Actual on-disk blob size, including the AES-GCM envelope header (20 bytes) and per-chunk
    /// tags (~16 bytes per 64 KiB chunk) when the drive is encrypted. Equal to <see cref="Size"/>
    /// for plaintext drives. Populated for storage-planning and incident-response math — NOT
    /// charged to user quota (that stays plaintext-denominated per STRG-026 #5).
    /// </summary>
    public long BlobSizeBytes { get; init; }

    public required string ContentHash { get; init; }
    public required string StorageKey { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public Guid CreatedBy { get; init; }
}
