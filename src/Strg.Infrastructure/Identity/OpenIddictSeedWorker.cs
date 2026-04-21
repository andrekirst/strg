using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenIddict.Abstractions;

namespace Strg.Infrastructure.Identity;

public sealed class OpenIddictSeedWorker(IServiceProvider services) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        if (await manager.FindByClientIdAsync("strg-default", cancellationToken) is null)
        {
            await manager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "strg-default",
                ClientSecret = null,
                DisplayName = "strg Default Client",
                // Implicit: strg is a first-party identity realm with no external OAuth app
                // approvals in v0.1. Also silences OpenIddict 7.x's missing-consent-type warning.
                ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                Permissions =
                {
                    OpenIddictConstants.Permissions.Endpoints.Token,
                    OpenIddictConstants.Permissions.GrantTypes.Password,
                    OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                    OpenIddictConstants.Permissions.Prefixes.Scope + "files.read",
                    OpenIddictConstants.Permissions.Prefixes.Scope + "files.write",
                    OpenIddictConstants.Permissions.Prefixes.Scope + "files.share",
                    OpenIddictConstants.Permissions.Prefixes.Scope + "tags.write",
                    OpenIddictConstants.Permissions.Prefixes.Scope + "admin",
                    OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OpenId,
                    OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.Email,
                    OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.Profile,
                    // offline_access is the OAuth/OIDC scope that gates refresh-token issuance.
                    // Without this client permission, the password-flow handler would rotate the
                    // access token but never emit a refresh token — the RefreshToken grant-type
                    // permission above would be unreachable.
                    OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess,
                },
            }, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
