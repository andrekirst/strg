using System.Security.Claims;

namespace Strg.Core.Identity;

/// <summary>
/// Extensions for reading strg-specific claims from a <see cref="ClaimsPrincipal"/>.
/// Uses only BCL types — no OpenIddict or ASP.NET Core dependency allowed in Strg.Core.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    private const string SubClaim = "sub";
    private const string TenantIdClaim = "tenant_id";
    private const string ScopeClaim = "scope";

    /// <summary>Returns the authenticated user's ID from the JWT <c>sub</c> claim.</summary>
    public static Guid GetUserId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirst(SubClaim)?.Value
            ?? throw new InvalidOperationException("The 'sub' claim is missing from the current principal."));

    /// <summary>Returns the tenant ID from the JWT <c>tenant_id</c> claim.</summary>
    public static Guid GetTenantId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirst(TenantIdClaim)?.Value
            ?? throw new InvalidOperationException("The 'tenant_id' claim is missing from the current principal."));

    /// <summary>
    /// Returns <see langword="true"/> when the principal holds the specified scope.
    /// Handles both space-separated single claims and multiple individual scope claims.
    /// </summary>
    public static bool HasScope(this ClaimsPrincipal user, string scope) =>
        user.FindAll(ScopeClaim)
            .Any(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                       .Contains(scope, StringComparer.Ordinal));
}
