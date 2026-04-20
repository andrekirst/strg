namespace Strg.Core.Domain;

public interface IFileVersionRepository
{
    Task<FileVersion?> GetAsync(Guid fileId, int versionNumber, CancellationToken ct = default);
    Task<IReadOnlyList<FileVersion>> ListAsync(Guid fileId, CancellationToken ct = default);
    Task AddAsync(FileVersion version, CancellationToken ct = default);
}
