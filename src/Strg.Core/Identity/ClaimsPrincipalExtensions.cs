using System.Security.Claims;
using Strg.Core.Constants;

namespace Strg.Core.Identity;

/// <summary>
/// Extensions for reading strg-specific claims from a <see cref="ClaimsPrincipal"/>.
/// Uses only BCL types — no OpenIddict or ASP.NET Core dependency allowed in Strg.Core.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    extension(ClaimsPrincipal user)
    {
        /// <summary>Returns the authenticated user's ID from the JWT <c>sub</c> claim.</summary>
        public Guid GetUserId() =>
            Guid.Parse(user.FindFirst(StrgClaimNames.Subject)?.Value
                       ?? throw new InvalidOperationException("The 'sub' claim is missing from the current principal."));

        /// <summary>Returns the tenant ID from the JWT <c>tenant_id</c> claim.</summary>
        public Guid GetTenantId() =>
            Guid.Parse(user.FindFirst(StrgClaimNames.TenantId)?.Value
                       ?? throw new InvalidOperationException("The 'tenant_id' claim is missing from the current principal."));

        /// <summary>
        /// Returns <see langword="true"/> when the principal holds the specified scope.
        /// Handles both space-separated single claims and multiple individual scope claims.
        /// </summary>
        public bool HasScope(string scope) =>
            user.FindAll(StrgClaimNames.Scope)
                .Any(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(scope, StringComparer.Ordinal));
    }
}
