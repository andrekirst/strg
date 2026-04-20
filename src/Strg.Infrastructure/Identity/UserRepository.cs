using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.Infrastructure.Identity;

public sealed class UserRepository(StrgDbContext db) : IUserRepository
{
    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> GetByEmailAsync(Guid tenantId, string email, CancellationToken ct = default)
        => db.Users.IgnoreQueryFilters()
                   .FirstOrDefaultAsync(u => u.TenantId == tenantId
                       && u.Email == email.ToLowerInvariant()
                       && !u.IsDeleted, ct);

    public async Task<IReadOnlyList<User>> ListAsync(Guid tenantId, CancellationToken ct = default)
        => await db.Users.Where(u => u.TenantId == tenantId).ToListAsync(ct);

    public Task AddAsync(User user, CancellationToken ct = default)
    {
        db.Users.Add(user);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(User user, CancellationToken ct = default)
    {
        db.Users.Update(user);
        return Task.CompletedTask;
    }

    public async Task SoftDeleteAsync(Guid id, CancellationToken ct = default)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is not null)
        {
            user.DeletedAt = DateTimeOffset.UtcNow;
        }
    }
}
