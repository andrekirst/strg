namespace Strg.Core.Domain;

/// <summary>
/// Surfaces the current request's tenant identity. Implementations live in the outer layers
/// (Strg.Infrastructure's HttpTenantContext for the HTTP stack, design-time stubs for EF tooling,
/// test doubles in test projects). Keeping the contract here lets Strg.Core entities and
/// Strg.Application handlers take a dependency on the port without pulling in an HTTP or
/// EF-specific implementation.
/// </summary>
public interface ITenantContext
{
    Guid TenantId { get; }
}
