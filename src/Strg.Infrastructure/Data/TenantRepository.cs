using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data;

public sealed class TenantRepository(StrgDbContext db) : ITenantRepository
{
    public Task<Tenant?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
        => db.Tenants.FirstOrDefaultAsync(t => t.Name == name, cancellationToken);
}
