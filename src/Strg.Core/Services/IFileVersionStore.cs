using Strg.Core.Domain;
using Strg.Core.Exceptions;

namespace Strg.Core.Services;

/// <summary>
/// Lifecycle orchestrator for <see cref="FileVersion"/> records. Sits one layer above the raw
/// repositories: the upload path has already written the blob (via <c>IEncryptingFileWriter</c>
/// or the plaintext equivalent) and knows its <paramref name="storageKey"/>, content hash, and
/// sizes. This store then atomically persists the version row AND charges the owner's quota,
/// so an out-of-memory crash between "blob on disk" and "row in DB" at worst leaves an orphan
/// blob (reaped by the cleanup job, STRG-026 #2) — never a row pointing at vanished bytes.
///
/// <para><b>Commit-order contract.</b> <see cref="CreateVersionAsync"/> wraps the DB insert AND
/// the <see cref="IQuotaService.CommitAsync"/> call in a single DbContext transaction. EF Core's
/// <c>ExecuteUpdateAsync</c> enlists in the ambient transaction, so a DB-side failure after the
/// quota UPDATE rolls both back atomically — no need for a compensating <c>ReleaseAsync</c> in
/// the caller's catch path.</para>
///
/// <para><b>Quota goes to the file owner.</b> Even if <paramref name="createdBy"/> (the uploader)
/// differs from <c>file.CreatedBy</c> (the owner) — a v0.2 ACL scenario — quota is charged to
/// the owner. This matches how shared cloud storage bills the one who owns the file, not the
/// last editor.</para>
/// </summary>
public interface IFileVersionStore
{
    /// <summary>
    /// Creates a new <see cref="FileVersion"/> row for <paramref name="file"/>, assigns the next
    /// monotonically-increasing version number, updates <see cref="FileItem.VersionCount"/>, and
    /// commits the plaintext <paramref name="size"/> against the file owner's quota — all in a
    /// single transaction. Throws <see cref="QuotaExceededException"/> on shortfall (transaction
    /// rolls back, DB state unchanged, caller should delete the already-written blob).
    /// </summary>
    /// <param name="file">The owning <see cref="FileItem"/>. Its <c>VersionCount</c> is bumped.</param>
    /// <param name="storageKey">Opaque provider-internal key produced by the blob writer.</param>
    /// <param name="contentHash">Hex-encoded SHA-256 of the plaintext content.</param>
    /// <param name="size">Plaintext byte count. Quota-relevant. See <see cref="FileVersion.Size"/>.</param>
    /// <param name="blobSizeBytes">Actual on-disk blob size (plaintext + envelope overhead for
    /// encrypted drives). NOT charged to quota. See <see cref="FileVersion.BlobSizeBytes"/>.</param>
    /// <param name="createdBy">User id of the uploader (may differ from <c>file.CreatedBy</c>).</param>
    Task<FileVersion> CreateVersionAsync(
        FileItem file,
        string storageKey,
        string contentHash,
        long size,
        long blobSizeBytes,
        Guid createdBy,
        CancellationToken cancellationToken = default);

    /// <summary>Versions for <paramref name="fileId"/>, newest first.</summary>
    Task<IReadOnlyList<FileVersion>> GetVersionsAsync(Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Single version by (fileId, versionNumber). Returns <c>null</c> if the version does not
    /// exist OR its file is outside the caller's tenant (the transitive tenant filter on the
    /// owning <see cref="FileItem"/> makes cross-tenant lookups indistinguishable from misses).
    /// </summary>
    Task<FileVersion?> GetVersionAsync(Guid fileId, int versionNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes older versions beyond <paramref name="keepCount"/> (newest first). Physical
    /// storage blobs are deleted BEFORE the DB rows — a crash between the two surfaces as an
    /// orphan row pointing at a vanished blob, which read-path callers can downgrade to a 404.
    /// The reverse order (DB first, then storage) would leak storage that no DB row references,
    /// which is unreachable and can only be cleaned by a full-scan reaper.
    ///
    /// <para><b>keepCount semantics.</b> <c>0</c> means "keep all" (retention disabled — the
    /// default policy), NOT "delete all". <c>1</c> keeps only the latest. Any N ≥ 1 keeps the N
    /// newest and prunes the rest.</para>
    /// </summary>
    Task PruneVersionsAsync(Guid fileId, int keepCount, CancellationToken cancellationToken = default);
}
