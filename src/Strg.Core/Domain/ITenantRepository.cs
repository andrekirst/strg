namespace Strg.Core.Domain;

public interface ITenantRepository
{
    /// <summary>
    /// Resolves a tenant by its unique name. Used pre-auth by flows that need to place a user
    /// in a specific tenant before any JWT exists — notably the self-registration endpoint and
    /// the first-run seed worker. Returns <see langword="null"/> when no tenant with that name
    /// exists. Name lookup is case-sensitive (matches the <c>UNIQUE(name)</c> index on the
    /// <c>tenants</c> table).
    /// </summary>
    Task<Tenant?> GetByNameAsync(string name, CancellationToken cancellationToken = default);
}
