using Microsoft.AspNetCore.Authorization;
using OpenIddict.Abstractions;

namespace Strg.Api.Auth;

/// <summary>
/// Authorization requirement that checks for a specific OpenIddict scope in the token.
/// </summary>
public sealed class ScopeRequirement(string scope) : IAuthorizationRequirement
{
    public string Scope { get; } = scope;
}

/// <summary>
/// Handler that satisfies <see cref="ScopeRequirement"/> using OpenIddict's HasScope extension.
/// </summary>
public sealed class ScopeRequirementHandler : AuthorizationHandler<ScopeRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ScopeRequirement requirement)
    {
        // Use OpenIddict's built-in scope evaluation (handles space-separated and multi-claim scopes)
        if (OpenIddict.Abstractions.OpenIddictExtensions.HasScope(context.User, requirement.Scope))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
