namespace Strg.Core.Constants;

/// <summary>
/// Canonical JWT claim names emitted by strg's token endpoint and read by downstream components.
/// Co-located here (not in the Infrastructure layer) because <see cref="Strg.Core"/> bans
/// external NuGet dependencies — <c>ClaimsPrincipalExtensions</c> would otherwise need to
/// duplicate the <c>sub</c>/<c>scope</c> strings that OpenIddict defines in
/// <c>OpenIddictConstants.Claims</c>. A single source of truth keeps the token-issuing side
/// (<c>TokenController</c>) and the token-reading side (<c>HttpTenantContext</c>,
/// <c>ClaimsPrincipalExtensions</c>) from drifting.
///
/// <para>
/// <see cref="TenantId"/> is strg-specific and has no OpenIddict equivalent. <see cref="Subject"/>,
/// <see cref="Scope"/>, <see cref="Role"/>, <see cref="Email"/>, and <see cref="Name"/> mirror the
/// standard JWT wire names that OpenIddict uses; they live here so consumers don't need to cross
/// the Core→Infrastructure boundary to reference them.
/// </para>
/// </summary>
public static class StrgClaimNames
{
    /// <summary>JWT <c>sub</c> — the authenticated user's ID.</summary>
    public const string Subject = "sub";

    /// <summary>Strg-specific claim carrying the authenticated user's tenant scope.</summary>
    public const string TenantId = "tenant_id";

    /// <summary>JWT <c>scope</c> — space-separated list of granted scopes.</summary>
    public const string Scope = "scope";

    /// <summary>JWT <c>role</c> — <see cref="Domain.UserRole"/> as a string.</summary>
    public const string Role = "role";

    /// <summary>JWT <c>email</c> — the user's lowercased email address.</summary>
    public const string Email = "email";

    /// <summary>JWT <c>name</c> — the user's display name.</summary>
    public const string Name = "name";
}
