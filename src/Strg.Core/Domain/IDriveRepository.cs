namespace Strg.Core.Domain;

public interface IDriveRepository
{
    Task<Drive?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Drive?> GetByNameAsync(Guid tenantId, string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Drive>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task AddAsync(Drive drive, CancellationToken cancellationToken = default);
    Task UpdateAsync(Drive drive, CancellationToken cancellationToken = default);
    Task SoftDeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
