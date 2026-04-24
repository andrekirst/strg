using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Strg.Core.Auditing;
using Strg.Core.Constants;
using Strg.Core.Identity;
using System.Net.Mime;
using System.Security.Claims;

namespace Strg.Api.Auth;

[ApiController]
public sealed class TokenController(
    IUserManager userManager,
    IAuditService auditService,
    ILogger<TokenController> logger) : ControllerBase
{
    /// <summary>
    /// Token endpoint passthrough — OpenIddict validates the request and calls
    /// <c>SignInAsync</c> with the principal produced here.
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
        var clientIp = GetClientIp();

        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            await TryLogLoginFailureAsync(request.Username, clientIp, cancellationToken);
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
            await TryLogLoginFailureAsync(request.Username, clientIp, cancellationToken);
            return InvalidGrant("The username or password is invalid.");
        }

        await TryLogLoginSuccessAsync(user.Id, user.TenantId, clientIp, cancellationToken);

        var principal = BuildPrincipal(user, request.GetScopes());
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private async Task<IActionResult> HandleRefreshTokenGrantAsync()
    {
        var clientIp = GetClientIp();

        // Authenticate with the existing refresh token principal so OpenIddict can rotate the
        // tokens. A valid refresh token reaching this point only proves the *token* is good —
        // the user behind it may have been soft-deleted, locked out, or role-downgraded since
        // the refresh token was issued. We MUST re-read the current user row and fail-closed on
        // any liveness issue; otherwise a stolen or stale refresh token survives those signals
        // until natural token expiry.
        var result = await HttpContext.AuthenticateAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        if (!result.Succeeded)
        {
            await TryLogLoginFailureAsync(null, clientIp, HttpContext.RequestAborted);
            return InvalidGrant("The refresh token is no longer valid.");
        }

        var subjectClaim = result.Principal!.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
        if (!Guid.TryParse(subjectClaim, out var userId))
        {
            await TryLogLoginFailureAsync(null, clientIp, HttpContext.RequestAborted);
            return InvalidGrant("The refresh token is no longer valid.");
        }

        var user = await userManager.FindForRefreshAsync(userId, HttpContext.RequestAborted);
        if (user is null)
        {
            await TryLogLoginFailureAsync(null, clientIp, HttpContext.RequestAborted);
            return InvalidGrant("The refresh token is no longer valid.");
        }

        await TryLogLoginSuccessAsync(user.Id, user.TenantId, clientIp, HttpContext.RequestAborted);

        // Rebuild claims from the fresh row so role changes, email updates, and tenant moves
        // propagate on the next refresh. Scopes are preserved from the incoming principal —
        // they were negotiated at the initial grant and are client/server contract, not
        // identity state.
        var principal = BuildPrincipal(user, result.Principal.GetScopes());
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
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
        identity.AddClaim(new Claim(StrgClaimNames.TenantId, user.TenantId.ToString()));

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

    /// <summary>
    /// Resolves the caller's IP address honouring the reverse-proxy deployment topology: real
    /// deployments sit behind nginx/traefik, which rewrites the socket peer address to its own
    /// upstream and passes the original client IP in <c>X-Forwarded-For</c>. Take the first
    /// hop — subsequent entries may be proxies between the client and our edge.
    /// </summary>
    private string? GetClientIp()
    {
        var forwardedFor = HttpContext.Request.Headers[StrgHeaderNames.XForwardedFor].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            var first = forwardedFor.Split(',', 2)[0].Trim();
            if (first.Length > 0)
            {
                return first;
            }
        }

        return HttpContext.Connection.RemoteIpAddress?.ToString();
    }

    // Audit writes are best-effort — an outage of the audit store must not turn into an auth
    // outage. Swallow and log; the auth decision stands regardless.
    private async Task TryLogLoginSuccessAsync(
        Guid userId,
        Guid tenantId,
        string? clientIp,
        CancellationToken cancellationToken)
    {
        try
        {
            await auditService.LogLoginSuccessAsync(userId, tenantId, clientIp, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to write login.success audit entry for user {UserId} in tenant {TenantId}",
                userId,
                tenantId);
        }
    }

    private async Task TryLogLoginFailureAsync(
        string? email,
        string? clientIp,
        CancellationToken cancellationToken)
    {
        try
        {
            await auditService.LogLoginFailureAsync(email, clientIp, cancellationToken);
        }
        catch (Exception ex)
        {
            // Log the outage but not the submitted email — an audit-write-failed log line shouldn't
            // re-introduce the enumeration vector the audit layer itself is taking pains to avoid.
            logger.LogError(ex, "Failed to write login.failure audit entry");
        }
    }
}
