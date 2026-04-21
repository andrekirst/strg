using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Strg.Infrastructure.Data;

// Used only by `dotnet ef` tooling when running outside the DI container.
// The connection string is never dialed — migrations add/script only load the model.
// UseOpenIddict() mirrors the production registration in Program.cs so the OpenIddict entity
// configurations (applications, authorizations, scopes, tokens) are part of the model EF Core
// diffs against when generating migrations. A previous regression shipped an initial migration
// missing OpenIddict tables because this factory lacked the call — see STRG-005 regen.
public sealed class StrgDbContextFactory : IDesignTimeDbContextFactory<StrgDbContext>
{
    public StrgDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<StrgDbContext>()
            .UseNpgsql("Host=localhost;Database=strg_design;Username=postgres;Password=postgres")
            .UseOpenIddict()
            .Options;

        return new StrgDbContext(options, new DesignTimeTenantContext());
    }

    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public Guid TenantId => Guid.Empty;
    }
}
