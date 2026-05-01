namespace Strg.Core.Domain;

/// <summary>
/// Persistence port for <see cref="ThumbnailEntry"/>. Per project convention, repositories DO
/// NOT call <c>SaveChangesAsync</c> — the caller (consumer / endpoint handler) commits.
/// </summary>
public interface IThumbnailRepository
{
    /// <summary>Stages a new entry for insertion. Caller commits.</summary>
    void Add(ThumbnailEntry entry);

    /// <summary>
    /// Returns the row matching <c>(fileVersionId, variant, format)</c> or <c>null</c>. Used by
    /// the REST endpoint to resolve a request and by the consumer to detect a re-delivery's
    /// pre-existing row after a <c>23505</c> catch.
    /// </summary>
    Task<ThumbnailEntry?> GetAsync(
        Guid fileVersionId,
        string variant,
        string format,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every row for a given <see cref="FileVersion"/>. Used by the REST endpoint's
    /// fallback path (when a specific variant is missing but others may exist) and by the
    /// extended <c>PruneVersionsAsync</c> loop to enumerate blob keys before transactional row removal.
    /// </summary>
    Task<IReadOnlyList<ThumbnailEntry>> GetByFileVersionAsync(
        Guid fileVersionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every row for a given <see cref="FileItem"/> (across all its versions). Used by
    /// <c>ThumbnailCleanupConsumer</c> on <c>FileDeletedEvent</c>.
    /// </summary>
    Task<IReadOnlyList<ThumbnailEntry>> GetByFileAsync(
        Guid fileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks each entry's <see cref="TenantedEntity.DeletedAt"/>. Caller commits.
    /// Idempotent: re-applying to already-soft-deleted rows is a no-op (the global filter
    /// excludes them so the input list itself never contains them on re-delivery).
    /// </summary>
    void SoftDeleteRange(IEnumerable<ThumbnailEntry> entries);
}
