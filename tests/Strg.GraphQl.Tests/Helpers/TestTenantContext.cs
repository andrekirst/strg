using Strg.Infrastructure.Data;

namespace Strg.GraphQl.Tests.Helpers;

internal sealed class TestTenantContext : ITenantContext
{
    public Guid TenantId { get; set; }

    // Single instance shared across all test classes to satisfy EF Core's global model cache,
    // which captures the ITenantContext reference at first model compilation.
    public static readonly TestTenantContext Shared = new();
}

// Marker for test classes that share a StrgDbContext and must run sequentially.
[Xunit.CollectionDefinition("database")]
public sealed class DatabaseCollection { }
