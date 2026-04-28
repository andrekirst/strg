namespace Strg.Core.Domain;

public sealed class FileVersion : Entity
{
    private string _storageKey = null!;
    private long _blobSizeBytes;

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
    ///
    /// <para><b>The accessor pair</b> follows the <c>FileItem.DriveId</c> precedent: <c>init</c>
    /// permits the canonical object-initializer creation pattern used by every <c>FileVersion</c>
    /// creation site (<c>StrgTusStore.FinalizeAsync</c>, <c>FileVersionStore</c>, the download
    /// fixture); <see cref="RebaseStorage"/> mutates the backing field directly so cross-drive
    /// move semantics can flip the storage envelope without a public/internal setter that any
    /// other call site could write to.</para>
    /// </summary>
    public long BlobSizeBytes
    {
        get => _blobSizeBytes;
        init => _blobSizeBytes = value;
    }

    public required string ContentHash { get; init; }

    /// <summary>
    /// Storage-backend locator for this version's bytes. The <c>init</c>+backing-field pattern
    /// matches <see cref="BlobSizeBytes"/> — see its remarks for the rationale. The only
    /// post-construction mutation site is <see cref="RebaseStorage"/>.
    /// </summary>
    public required string StorageKey
    {
        get => _storageKey;
        init => _storageKey = value;
    }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public Guid CreatedBy { get; init; }

    /// <summary>
    /// Cross-drive bytes-relocation primitive used by <c>MoveFileHandler.MoveFileCrossDriveAsync</c>.
    /// Plaintext <see cref="Size"/> and <see cref="ContentHash"/> are invariant under cross-drive —
    /// only the storage envelope changes (encryption posture + storage key). The mutation is
    /// restricted to this method by the <c>init</c>+backing-field pattern.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="newStorageKey"/> is empty or whitespace.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="newBlobSize"/> is negative.
    /// </exception>
    public void RebaseStorage(string newStorageKey, long newBlobSize)
    {
        if (string.IsNullOrWhiteSpace(newStorageKey))
        {
            throw new ArgumentException("Storage key must not be empty or whitespace.", nameof(newStorageKey));
        }
        if (newBlobSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newBlobSize), newBlobSize, "Blob size must not be negative.");
        }

        _storageKey = newStorageKey;
        _blobSizeBytes = newBlobSize;
    }
}
