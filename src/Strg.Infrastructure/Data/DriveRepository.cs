using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data;

public sealed class DriveRepository(StrgDbContext db) : IDriveRepository
{
    public Task<Drive?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => db.Drives.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    /// <summary>
    /// Uniqueness lookup that spans active AND soft-deleted drives within a tenant. Bypasses the
    /// global tenant/soft-delete filter so a soft-deleted drive named "x" still blocks creation
    /// of a new drive named "x" — otherwise un-soft-delete (e.g. an admin restore) could produce
    /// two drives with identical (TenantId, Name), violating the unique index. Returning the
    /// soft-deleted match lets callers surface a clearer error than a 23505 constraint violation.
    /// </summary>
    public Task<Drive?> GetByNameAsync(Guid tenantId, string name, CancellationToken cancellationToken = default)
        => db.Drives.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.Name == name, cancellationToken);

    public async Task<IReadOnlyList<Drive>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => await db.Drives.ToListAsync(cancellationToken);

    public Task AddAsync(Drive drive, CancellationToken cancellationToken = default)
    {
        db.Drives.Add(drive);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Drive drive, CancellationToken cancellationToken = default)
    {
        db.Drives.Update(drive);
        return Task.CompletedTask;
    }

    public async Task SoftDeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var drive = await db.Drives.FindAsync([id], cancellationToken);
        drive?.DeletedAt = DateTimeOffset.UtcNow;
    }
}
