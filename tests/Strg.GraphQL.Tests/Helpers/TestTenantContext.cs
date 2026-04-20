using Strg.Infrastructure.Data;

namespace Strg.GraphQL.Tests.Helpers;

internal sealed class TestTenantContext : ITenantContext
{
    public Guid TenantId { get; set; }
}
