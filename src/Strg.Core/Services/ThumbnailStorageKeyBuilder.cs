namespace Strg.Core.Services;

/// <summary>
/// Centralised builder for thumbnail storage keys. The format
/// <c>thumbnails/{driveId}/{fileVersionId}/{variant}.{format}</c> is the single source of truth —
/// callers MUST NOT concatenate strings. Same discipline as <c>StorageKey</c> handling in the
/// upload pipeline.
///
/// <para><b>Caller obligation.</b> The returned key is a logical path, not a sanitised
/// <c>StoragePath</c>. Every call site MUST wrap the result in <c>StoragePath.Parse(...)</c>
/// before passing it to <c>IStorageProvider</c>. The generator's input variants come from a
/// closed whitelist (<see cref="ThumbnailVariants.IsKnown"/>) but defence-in-depth applies —
/// path-traversal protection lives at the storage boundary.</para>
/// </summary>
public static class ThumbnailStorageKeyBuilder
{
    /// <summary>
    /// Build the canonical storage key for a thumbnail blob.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="driveId"/> or <paramref name="fileVersionId"/> is <see cref="Guid.Empty"/>;
    /// <paramref name="variant"/> is not in <see cref="ThumbnailVariants.All"/>;
    /// <paramref name="format"/> is not in the known formats whitelist.
    /// </exception>
    public static string Build(Guid driveId, Guid fileVersionId, string variant, string format)
    {
        if (driveId == Guid.Empty)
        {
            throw new ArgumentException("Drive id must not be empty.", nameof(driveId));
        }
        if (fileVersionId == Guid.Empty)
        {
            throw new ArgumentException("File version id must not be empty.", nameof(fileVersionId));
        }
        if (!ThumbnailVariants.IsKnown(variant))
        {
            throw new ArgumentException(
                $"Unknown thumbnail variant '{variant}'.", nameof(variant));
        }
        if (!ThumbnailFormats.IsKnown(format))
        {
            throw new ArgumentException(
                $"Unknown thumbnail format '{format}'.", nameof(format));
        }

        return $"thumbnails/{driveId:D}/{fileVersionId:D}/{variant}.{format}";
    }
}
