namespace Strg.Core.Domain;

public interface IFileRepository
{
    Task<FileItem?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<FileItem?> GetByPathAsync(Guid driveId, string path, CancellationToken ct = default);
    Task<IReadOnlyList<FileItem>> ListByParentAsync(Guid driveId, Guid? parentId, CancellationToken ct = default);
    Task AddAsync(FileItem file, CancellationToken ct = default);
    Task UpdateAsync(FileItem file, CancellationToken ct = default);
    Task SoftDeleteAsync(Guid id, CancellationToken ct = default);
}
