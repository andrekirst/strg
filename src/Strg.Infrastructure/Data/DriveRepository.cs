using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data;

public sealed class DriveRepository(StrgDbContext db) : IDriveRepository
{
    public Task<Drive?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => db.Drives.FirstOrDefaultAsync(d => d.Id == id, ct);

    /// <summary>
    /// Bypasses the global tenant/soft-delete query filter intentionally: this method
    /// is used for uniqueness checks, including against soft-deleted names to prevent
    /// re-use of deleted drive names within the same tenant.
    /// </summary>
    public Task<Drive?> GetByNameAsync(Guid tenantId, string name, CancellationToken ct = default)
        => db.Drives.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.Name == name && !d.IsDeleted, ct);

    public async Task<IReadOnlyList<Drive>> ListAsync(Guid tenantId, CancellationToken ct = default)
        => await db.Drives.ToListAsync(ct);

    public Task AddAsync(Drive drive, CancellationToken ct = default)
    {
        db.Drives.Add(drive);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Drive drive, CancellationToken ct = default)
    {
        db.Drives.Update(drive);
        return Task.CompletedTask;
    }

    public async Task SoftDeleteAsync(Guid id, CancellationToken ct = default)
    {
        var drive = await db.Drives.FindAsync([id], ct);
        if (drive is not null)
        {
            drive.DeletedAt = DateTimeOffset.UtcNow;
        }
    }
}
