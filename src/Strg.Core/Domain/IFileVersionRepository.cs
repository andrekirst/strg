namespace Strg.Core.Domain;

public interface IFileVersionRepository
{
    Task<FileVersion?> GetAsync(Guid fileId, int versionNumber, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileVersion>> ListAsync(Guid fileId, CancellationToken cancellationToken = default);
    Task AddAsync(FileVersion version, CancellationToken cancellationToken = default);
}
