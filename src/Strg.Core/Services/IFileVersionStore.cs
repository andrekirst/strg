using Strg.Core.Domain;
using Strg.Core.Exceptions;

namespace Strg.Core.Services;

/// <summary>
/// Lifecycle orchestrator for <see cref="FileVersion"/> records. Sits one layer above the raw
/// repositories: the upload path has already written the blob (via <c>IEncryptingFileWriter</c>
/// or the plaintext equivalent) and knows its <c>storageKey</c>, content hash, and
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
/// <para><b>Quota goes to the file owner.</b> Even if <c>createdBy</c> (the uploader)
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
    ///
    /// <para><b><paramref name="file"/> parameter state on exception.</b> The caller's
    /// <paramref name="file"/> reference is mutated in-memory BEFORE the transaction commits —
    /// <see cref="FileItem.VersionCount"/>, <see cref="FileItem.Size"/>,
    /// <see cref="FileItem.ContentHash"/>, and <see cref="FileItem.StorageKey"/> are assigned to
    /// the new-version values. If the transaction rolls back (quota exceeded or a later DB error),
    /// those mutations persist on the caller's object but NOT in the DB. The reference is thus in
    /// an indeterminate state; reload from <see cref="IFileRepository.GetByIdAsync"/> before
    /// reusing it.</para>
    /// </summary>
    /// <param name="file">The owning <see cref="FileItem"/>. Its <c>VersionCount</c> is bumped.</param>
    /// <param name="storageKey">Opaque provider-internal key produced by the blob writer.</param>
    /// <param name="contentHash">Hex-encoded SHA-256 of the plaintext content.</param>
    /// <param name="size">Plaintext byte count. Quota-relevant. See <see cref="FileVersion.Size"/>.</param>
    /// <param name="blobSizeBytes">Actual on-disk blob size (plaintext + envelope overhead for
    /// encrypted drives). NOT charged to quota. See <see cref="FileVersion.BlobSizeBytes"/>.</param>
    /// <param name="createdBy">User id of the uploader (may differ from <c>file.CreatedBy</c>).</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
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
    ///
    /// <para><b>Per-version atomicity (STRG-043 M1).</b> Each to-prune version is its own atomic
    /// unit: blob delete → open tx → remove row + release quota → commit. Mid-loop failure at
    /// iteration k leaves a crisp partial state: iterations <c>[0..k-1]</c> fully committed
    /// (blob gone, row removed, quota released), iteration <c>k</c> not attempted or rolled back,
    /// iterations <c>[k+1..]</c> untouched. Retry resumes by re-pruning the same "beyond
    /// keepCount" tail. Because the batch is ordered newest-of-old → very-oldest, a partial
    /// failure leaves a middle gap in <see cref="FileVersion.VersionNumber"/> (e.g., after partial
    /// prune of {1..7} the surviving rows might be {1,2,3,4,5,8,9,10}) — read paths tolerate gaps
    /// and never rely on contiguity.</para>
    ///
    /// <para><b>Audit emission.</b> A single <c>file_version.pruned</c> audit entry is written
    /// after the loop completes successfully, carrying
    /// <c>retained_count=N; pruned_count=M; bytes_released=B</c>. <b>Emitted only on full
    /// success</b> — partial-failure paths propagate the exception and no audit row is written,
    /// because a half-baked row would mislead a reader into treating an interrupted prune as
    /// complete. The committed <c>[0..k-1]</c> iterations remain observable via the DB state
    /// itself; the next successful retry emits its own audit entry reflecting the final
    /// <c>pruned_count</c>. Audit-store outages on the post-loop write are swallowed and logged
    /// — the prune itself has already committed, and failing the primary op on a logging concern
    /// would turn a monitoring outage into an availability one.</para>
    /// </summary>
    Task PruneVersionsAsync(Guid fileId, int keepCount, CancellationToken cancellationToken = default);
}
