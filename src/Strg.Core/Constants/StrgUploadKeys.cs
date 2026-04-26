namespace Strg.Core.Constants;

/// <summary>
/// Storage-key shapes for the TUS upload pipeline (STRG-034) and the abandoned-upload sweep
/// (STRG-036). Both consumers MUST import this constant rather than inline the string literals —
/// drift between the writer (STRG-034) and the reaper (STRG-036) on the temp-key prefix would
/// silently strand orphan ciphertext.
///
/// <para>The <see cref="TempPrefix"/> is the load-bearing string here: STRG-036's sweep enumerates
/// keys under it and matches them against pending-upload rows. The <see cref="FinalKey"/> shape is
/// internal to the upload pipeline (the FileVersion row holds the resolved key), so it is free to
/// evolve in lockstep with the writer.</para>
///
/// <para><b>Why is the final key derived from FileItem.Id, not FileItem.Path?</b> Path is mutable
/// (rename, move). A final key derived from a mutable field would force <c>MoveAsync</c> on every
/// rename, which is acceptable on a local filesystem but costly on S3 (copy-then-delete). Pinning
/// the key to the immutable <c>FileItem.Id</c> means rename/move are pure DB operations and the
/// blob never moves until the version itself is replaced.</para>
/// </summary>
public static class StrgUploadKeys
{
    /// <summary>
    /// Prefix for in-flight TUS upload temp blobs. STRG-036's sweep imports this verbatim.
    /// Kept lowercase + slash-terminated so prefix-match queries on storage backends that treat
    /// keys as opaque strings (S3, in-memory) work without extra normalization.
    /// </summary>
    public const string TempPrefix = "uploads/temp/";

    /// <summary>
    /// Builds the temp-namespaced storage key where the encrypting writer lands the ciphertext
    /// envelope before promotion to the final key. Both Guids are formatted with <c>N</c> (32
    /// lowercase hex chars, no dashes) so the resulting path passes <see cref="Storage.StoragePath.Parse"/>
    /// and stays free of dash-separator quirks on opinionated storage backends.
    /// </summary>
    public static string TempKey(Guid driveId, Guid uploadId)
        => $"{TempPrefix}{driveId:N}/{uploadId:N}";

    /// <summary>
    /// Builds the final storage key for a committed file version. Anchored on
    /// <c>FileItem.Id</c> + <see cref="Domain.FileVersion.VersionNumber"/> so rename and move are
    /// pure DB operations — the blob never moves until the version itself is replaced.
    /// </summary>
    public static string FinalKey(Guid driveId, Guid fileId, int versionNumber)
        => $"drives/{driveId:N}/files/{fileId:N}/v{versionNumber}";

    /// <summary>
    /// Returns <c>true</c> if <paramref name="storageKey"/> sits under the temp namespace.
    /// Used by callers that want to assert "this version's storage key is the final, promoted
    /// key" without re-deriving the shape locally.
    /// </summary>
    public static bool IsTempKey(string storageKey)
    {
        ArgumentNullException.ThrowIfNull(storageKey);
        return storageKey.StartsWith(TempPrefix, StringComparison.Ordinal);
    }
}
