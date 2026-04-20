using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Strg.Infrastructure.Data;

// Used only by `dotnet ef` tooling when running outside the DI container.
// The connection string is never dialed — migrations add/script only load the model.
public sealed class StrgDbContextFactory : IDesignTimeDbContextFactory<StrgDbContext>
{
    public StrgDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<StrgDbContext>()
            .UseNpgsql("Host=localhost;Database=strg_design;Username=postgres;Password=postgres")
            .Options;

        return new StrgDbContext(options, new DesignTimeTenantContext());
    }

    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public Guid TenantId => Guid.Empty;
    }
}
