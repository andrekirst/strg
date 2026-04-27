namespace Strg.Core.Domain;

public interface IFileRepository
{
    Task<FileItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<FileItem?> GetByPathAsync(Guid driveId, string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileItem>> ListByParentAsync(Guid driveId, Guid? parentId, CancellationToken cancellationToken = default);
    Task AddAsync(FileItem file, CancellationToken cancellationToken = default);
    Task UpdateAsync(FileItem file, CancellationToken cancellationToken = default);
    Task SoftDeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams every <see cref="FileItem"/> in <paramref name="driveId"/> whose
    /// <see cref="FileItem.Path"/> starts with <paramref name="pathPrefix"/>. Caller MUST
    /// pre-anchor the prefix with a trailing <c>/</c> — paths are stored without a trailing
    /// separator (<c>"docs"</c>, <c>"docs/sub/notes.txt"</c>) per <c>StoragePath.Normalize</c>,
    /// so an unanchored prefix would match sibling rows whose paths share a common stem
    /// (<c>"docsbackup"</c> would match the prefix <c>"docs"</c>).
    ///
    /// <para>Streamed, not buffered: the recursive-delete handler can mutate large directory
    /// subtrees without loading every descendant into memory at once. The global tenant +
    /// soft-delete query filters on <c>StrgDbContext</c> apply, so cross-tenant rows and
    /// already-deleted rows are skipped automatically.</para>
    /// </summary>
    IAsyncEnumerable<FileItem> GetDescendantsAsync(
        Guid driveId,
        string pathPrefix,
        CancellationToken cancellationToken = default);
}
