namespace Strg.Infrastructure.Data;

public interface ITenantContext
{
    Guid TenantId { get; }
}
