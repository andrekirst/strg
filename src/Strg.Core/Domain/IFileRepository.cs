namespace Strg.Core.Domain;

public interface IFileRepository
{
    Task<FileItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<FileItem?> GetByPathAsync(Guid driveId, string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileItem>> ListByParentAsync(Guid driveId, Guid? parentId, CancellationToken cancellationToken = default);
    Task AddAsync(FileItem file, CancellationToken cancellationToken = default);
    Task UpdateAsync(FileItem file, CancellationToken cancellationToken = default);
    Task SoftDeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
