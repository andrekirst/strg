using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Strg.Core.Identity;
using System.Net.Mime;
using System.Security.Claims;

namespace Strg.Api.Auth;

[ApiController]
public sealed class TokenController(IUserManager userManager) : ControllerBase
{
    // Custom claim type — kept in sync with HttpTenantContext which reads "tenant_id"
    // from the authenticated user's claims.
    private const string TenantIdClaim = "tenant_id";

    /// <summary>
    /// Token endpoint passthrough — OpenIddict validates the request and calls
    /// <see cref="SignInAsync"/> with the principal produced here.
    /// </summary>
    [HttpPost("~/connect/token")]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> Exchange()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("OpenIddict server request could not be retrieved.");

        if (request.IsPasswordGrantType())
        {
            return await HandlePasswordGrantAsync(request, HttpContext.RequestAborted);
        }

        if (request.IsRefreshTokenGrantType())
        {
            return await HandleRefreshTokenGrantAsync();
        }

        return BadRequest(new { error = OpenIddictConstants.Errors.UnsupportedGrantType });
    }

    private async Task<IActionResult> HandlePasswordGrantAsync(
        OpenIddictRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return InvalidGrant("The username or password is invalid.");
        }

        // ValidateCredentialsAsync is the single-timing-envelope entry point: it owns the lookup,
        // the dummy-verify-on-miss, lockout bookkeeping, and counter reset on success. Do NOT
        // separately call FindForLoginAsync / ValidatePasswordAsync / RecordFailedLoginAsync /
        // ResetFailedLoginsAsync here — that would re-introduce the timing oracle the consolidated
        // method exists to defeat.
        var user = await userManager.ValidateCredentialsAsync(request.Username, request.Password, cancellationToken);
        if (user is null)
        {
            return InvalidGrant("The username or password is invalid.");
        }

        var principal = BuildPrincipal(user, request.GetScopes());
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private async Task<IActionResult> HandleRefreshTokenGrantAsync()
    {
        // Authenticate with the existing refresh token principal so that
        // OpenIddict can rotate the tokens.
        var result = await HttpContext.AuthenticateAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        if (!result.Succeeded)
        {
            return InvalidGrant("The refresh token is no longer valid.");
        }

        var identity = new ClaimsIdentity(
            result.Principal!.Claims,
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: OpenIddictConstants.Claims.Name,
            roleType: OpenIddictConstants.Claims.Role);

        return SignIn(new ClaimsPrincipal(identity),
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static ClaimsPrincipal BuildPrincipal(
        Core.Domain.User user,
        System.Collections.Immutable.ImmutableArray<string> requestedScopes)
    {
        var identity = new ClaimsIdentity(
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: OpenIddictConstants.Claims.Name,
            roleType: OpenIddictConstants.Claims.Role);

        identity.AddClaim(new Claim(OpenIddictConstants.Claims.Subject, user.Id.ToString()));
        identity.AddClaim(new Claim(OpenIddictConstants.Claims.Email, user.Email));
        identity.AddClaim(new Claim(OpenIddictConstants.Claims.Name, user.DisplayName));
        identity.AddClaim(new Claim(OpenIddictConstants.Claims.Role, user.Role.ToString()));
        identity.AddClaim(new Claim(TenantIdClaim, user.TenantId.ToString()));

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(requestedScopes);

        // OpenIddict requires each claim to declare which token(s) it ships in. For v0.1 we emit
        // all identity claims to the access token; ID-token shaping is out of scope for STRG-014.
        foreach (var claim in principal.Claims)
        {
            claim.SetDestinations(OpenIddictConstants.Destinations.AccessToken);
        }

        return principal;
    }

    private ForbidResult InvalidGrant(string description) => Forbid(
        authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
        properties: new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] =
                OpenIddictConstants.Errors.InvalidGrant,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }));
}
