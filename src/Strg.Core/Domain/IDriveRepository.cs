namespace Strg.Core.Domain;

public interface IDriveRepository
{
    Task<Drive?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Drive?> GetByNameAsync(Guid tenantId, string name, CancellationToken ct = default);
    Task<IReadOnlyList<Drive>> ListAsync(Guid tenantId, CancellationToken ct = default);
    Task AddAsync(Drive drive, CancellationToken ct = default);
    Task UpdateAsync(Drive drive, CancellationToken ct = default);
    Task SoftDeleteAsync(Guid id, CancellationToken ct = default);
}
