using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.Infrastructure.Identity;

public sealed class UserRepository(StrgDbContext db) : IUserRepository
{
    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    // IgnoreQueryFilters(): the login path runs before ITenantContext is populated (no JWT yet),
    // so the global tenant filter would resolve against an empty TenantId. TenantId and IsDeleted
    // are re-applied inline below so the bypass does not widen the search.
    public Task<User?> GetByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken = default)
        => db.Users.IgnoreQueryFilters()
                   .FirstOrDefaultAsync(u => u.TenantId == tenantId
                       && u.Email == email.ToLowerInvariant()
                       && !u.IsDeleted, cancellationToken);

    public async Task<IReadOnlyList<User>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => await db.Users.Where(u => u.TenantId == tenantId).ToListAsync(cancellationToken);

    public Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        db.Users.Add(user);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(User user, CancellationToken cancellationToken = default)
    {
        db.Users.Update(user);
        return Task.CompletedTask;
    }

    public async Task SoftDeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FindAsync([id], cancellationToken);
        if (user is not null)
        {
            user.DeletedAt = DateTimeOffset.UtcNow;
        }
    }
}
