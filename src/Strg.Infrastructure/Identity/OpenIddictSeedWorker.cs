using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenIddict.Abstractions;
using Strg.Core.Constants;

namespace Strg.Infrastructure.Identity;

/// <summary>
/// Seeds the <c>strg-default</c> OpenIddict application on first boot so first-party callers
/// (CLI, self-hosted admin UI, desktop sync) have a client registration to present at
/// <c>/connect/token</c>.
///
/// <para><b>Client type — Public.</b> First-party callers run in environments that cannot keep a
/// secret: a CLI installed on a developer workstation, a SPA whose JS bundle is downloadable, a
/// native desktop app whose binary is shippable. A confidential client would leak its secret the
/// moment the first distributable shipped. The descriptor pins
/// <see cref="OpenIddictConstants.ClientTypes.Public"/> explicitly instead of relying on
/// <c>ClientSecret == null</c> inference — without the explicit pin, a future edit that sets a
/// secret would silently flip the registration to confidential at
/// <see cref="IOpenIddictApplicationManager.CreateAsync(OpenIddictApplicationDescriptor, CancellationToken)"/>
/// time. With the pin, OpenIddict rejects the mismatch at seed time instead.</para>
///
/// <para><b>Password grant threat model.</b> The default client permits the ROPC password grant
/// because the v0.1 CLI + built-in admin UI need a non-browser login path. The server-side
/// brute-force guard lives in <c>UserManager.RecordFailedLoginAsync</c> (5-then-10-failure
/// tiered lockout) and authorization on privileged endpoints keys off the user's
/// <see cref="Strg.Core.Domain.UserRole"/> claim, not the requested scope. External / third-party
/// apps should register their own clients and use authorization-code + PKCE — password grant on
/// <c>strg-default</c> is a first-party-only convenience, not a general-purpose compatibility
/// hatch.</para>
///
/// <para><b>PKCE pinned at client level.</b> The descriptor sets
/// <see cref="OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange"/> even though
/// password grant does not exchange codes. It is defence-in-depth against a future refactor that
/// removes or narrows <c>RequireProofKeyForCodeExchange</c> at the server level (see
/// <see cref="OpenIddictConfiguration"/>): with the requirement pinned on the descriptor, this
/// specific client remains PKCE-only if someone later grants it
/// <see cref="OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode"/>, independent of
/// what the server-wide default becomes.</para>
///
/// <para><b>Seed idempotency.</b> The <see cref="IOpenIddictApplicationManager.FindByClientIdAsync(string, CancellationToken)"/>
/// short-circuit keeps this hosted service safe on every boot, on rolling restarts, and on
/// multi-replica startups — the unique index on <c>OpenIddictApplications.ClientId</c> would
/// reject duplicate inserts anyway, but the pre-check avoids the noise of a caught exception on
/// normal startup. No mutation on subsequent boots — an operator who hand-edits the row (e.g. to
/// grant a trusted internal tool an extra scope) is not overwritten by the next deploy; any
/// update to the canonical descriptor must be shipped as a separate migration.</para>
///
/// <para><b>Admin scope retention.</b> The <c>admin</c> scope is granted to this client
/// intentionally: the built-in v0.1 admin UI authenticates through the same client. Authorization
/// on admin endpoints keys off the user's role claim (<see cref="Strg.Core.Domain.UserRole.SuperAdmin"/>),
/// not the requested scope — so granting the scope to every first-party caller is non-harmful
/// (the scope is requestable by any authenticated user, but only SuperAdmin users pass the
/// endpoint-level policy). A future v0.2 split that moves admin tooling onto a dedicated client
/// should drop <c>admin</c> from this list at that point.</para>
/// </summary>
public sealed class OpenIddictSeedWorker(IServiceProvider services) : IHostedService
{
    internal const string DefaultClientId = "strg-default";

    /// <summary>
    /// STRG-073 — dedicated OpenIddict client used exclusively by
    /// <c>BasicAuthJwtBridgeMiddleware</c> to exchange cached Basic-Auth credentials for a JWT.
    /// Kept separate from <see cref="DefaultClientId"/> so the bridge's password-grant surface
    /// has the narrowest-possible permission set: only the three scopes WebDAV reads/writes need
    /// (<c>files.read</c>, <c>files.write</c>, <c>tags.write</c>) and <em>no</em> <c>admin</c> or
    /// <c>offline_access</c> — a stolen Basic-Auth credential cannot promote itself into an
    /// admin-scoped access token, and cannot mint a refresh token that outlives the 14-minute
    /// cache window.
    /// </summary>
    internal const string WebDavInternalClientId = "webdav-internal";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        await EnsureAsync(manager, DefaultClientId, BuildDefaultDescriptor, cancellationToken);
        await EnsureAsync(manager, WebDavInternalClientId, BuildWebDavInternalDescriptor, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task EnsureAsync(
        IOpenIddictApplicationManager manager,
        string clientId,
        Func<OpenIddictApplicationDescriptor> descriptorFactory,
        CancellationToken cancellationToken)
    {
        if (await manager.FindByClientIdAsync(clientId, cancellationToken) is not null)
        {
            return;
        }
        await manager.CreateAsync(descriptorFactory(), cancellationToken);
    }

    // Exposed as internal so integration tests can assert that the seeded row matches the
    // canonical descriptor without coupling the test to the CreateAsync path.
    internal static OpenIddictApplicationDescriptor BuildDefaultDescriptor() => new()
    {
        ClientId = DefaultClientId,
        ClientType = OpenIddictConstants.ClientTypes.Public,
        ClientSecret = null,
        DisplayName = "strg Default Client",
        // Implicit: strg is a first-party identity realm with no external OAuth app approvals in
        // v0.1. Also silences OpenIddict 7.x's missing-consent-type warning.
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
            // offline_access is the OAuth/OIDC scope that gates refresh-token issuance. Without
            // this client permission, the password-flow handler would rotate the access token but
            // never emit a refresh token — the RefreshToken grant-type permission above would be
            // unreachable.
            OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess,
        },
        Requirements =
        {
            OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange,
        },
    };

    /// <summary>
    /// STRG-073 — descriptor for the <c>webdav-internal</c> client consumed exclusively by the
    /// in-process Basic-Auth → JWT bridge. Intentionally narrower than
    /// <see cref="BuildDefaultDescriptor"/>:
    /// <list type="bullet">
    ///   <item><description><b>No RefreshToken grant.</b> The bridge caches tokens for
    ///     14 minutes (<c>access_token_lifetime - 60s</c>) and re-exchanges credentials on cache
    ///     miss. A refresh-token path would create a long-lived credential surface whose value
    ///     extends beyond the cache's invalidation window and would survive a password change
    ///     until token expiry — the cache invalidation hook can only evict what the cache knows
    ///     about, not tokens OpenIddict has issued to other holders.</description></item>
    ///   <item><description><b>No <c>admin</c> scope.</b> WebDAV clients cannot reach admin
    ///     endpoints regardless (the admin API is outside <c>/dav</c>), and binding the scope
    ///     to this client would let a compromised Basic-Auth credential mint an admin access
    ///     token usable against <c>/graphql</c> mutations that key off scope rather than
    ///     request path.</description></item>
    ///   <item><description><b>No <c>offline_access</c> / <c>openid</c> / <c>email</c> /
    ///     <c>profile</c>.</b> Those scopes are the OIDC identity-delegation surface; WebDAV
    ///     needs only the resource-access scopes.</description></item>
    ///   <item><description><b>No PKCE pin.</b> This client lacks the authorization-code grant
    ///     permission entirely, so the PKCE requirement would be unreachable — dead pin noise.
    ///     The absence is load-bearing: adding <c>GrantTypes.AuthorizationCode</c> later must
    ///     also add PKCE, but the code-path would also need revisiting end-to-end (bridge would
    ///     need a redirect handler), so deferring the pin until the surface exists is fine.</description></item>
    /// </list>
    /// Public client (no secret): the bridge runs inside the same process as the token endpoint,
    /// and a process-local secret would be security theatre — any attacker with the ability to
    /// read the bridge's config can already read the database.
    /// </summary>
    internal static OpenIddictApplicationDescriptor BuildWebDavInternalDescriptor()
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = WebDavInternalClientId,
            ClientType = OpenIddictConstants.ClientTypes.Public,
            ClientSecret = null,
            DisplayName = "strg WebDAV Internal Bridge",
            ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
            Permissions =
            {
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.GrantTypes.Password,
            },
        };

        // STRG-073 fold-in #1 — scope permissions are enumerated from the single source of truth
        // in Strg.Core.Constants.WebDavScopes.All so the bridge's form-scope param cannot drift
        // from the granted permission set. A drift (bridge asks for X not in Y, or Y is wider than
        // X) is silent: OpenIddict rejects the token with "invalid_scope" at request time, WebDAV
        // clients see a generic 401, and the cause lives in two files that only diff-review would
        // catch. Iterating the same array here removes that surface entirely.
        foreach (var scope in WebDavScopes.All)
        {
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + scope);
        }

        return descriptor;
    }
}
