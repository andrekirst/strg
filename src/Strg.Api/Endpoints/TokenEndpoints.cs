using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Strg.Api.RateLimiting;
using Strg.Core.Auditing;
using Strg.Core.Constants;
using Strg.Core.Identity;
using System.Collections.Immutable;
using System.Net.Mime;
using System.Security.Claims;

namespace Strg.Api.Endpoints;

/// <summary>
/// OpenIddict token-endpoint passthrough (STRG-014). OpenIddict validates the request via
/// <c>EnableTokenEndpointPassthrough()</c> and delegates principal construction here; the
/// response JSON is produced by OpenIddict's server handler observing the
/// <c>HttpContext.SignInAsync</c> call issued via <see cref="Results.SignIn"/>.
/// </summary>
public static class TokenEndpoints
{
    public static IEndpointRouteBuilder MapTokenEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/connect/token", ExchangeAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Auth)
            .WithName("Exchange")
            .WithTags("Auth")
            .Produces(StatusCodes.Status200OK, contentType: MediaTypeNames.Application.Json)
            .Produces(StatusCodes.Status400BadRequest, contentType: MediaTypeNames.Application.Json);

        return app;
    }

    private static async Task<IResult> ExchangeAsync(
        HttpContext httpContext,
        IUserManager userManager,
        IAuditService auditService,
        ILogger<TokenEndpointsLogCategory> logger,
        CancellationToken cancellationToken)
    {
        var request = httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("OpenIddict server request could not be retrieved.");

        if (request.IsPasswordGrantType())
        {
            return await HandlePasswordGrantAsync(httpContext, request, userManager, auditService, logger, cancellationToken);
        }

        if (request.IsRefreshTokenGrantType())
        {
            return await HandleRefreshTokenGrantAsync(httpContext, userManager, auditService, logger);
        }

        return Results.BadRequest(new { error = OpenIddictConstants.Errors.UnsupportedGrantType });
    }

    private static async Task<IResult> HandlePasswordGrantAsync(
        HttpContext httpContext,
        OpenIddictRequest request,
        IUserManager userManager,
        IAuditService auditService,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var clientIp = GetClientIp(httpContext);

        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            await TryLogLoginFailureAsync(auditService, logger, request.Username, clientIp, cancellationToken);
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
            await TryLogLoginFailureAsync(auditService, logger, request.Username, clientIp, cancellationToken);
            return InvalidGrant("The username or password is invalid.");
        }

        await TryLogLoginSuccessAsync(auditService, logger, user.Id, user.TenantId, clientIp, cancellationToken);

        var principal = BuildPrincipal(user, request.GetScopes());
        return Results.SignIn(principal, properties: null, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> HandleRefreshTokenGrantAsync(
        HttpContext httpContext,
        IUserManager userManager,
        IAuditService auditService,
        ILogger logger)
    {
        var clientIp = GetClientIp(httpContext);

        // Authenticate with the existing refresh token principal so OpenIddict can rotate the
        // tokens. A valid refresh token reaching this point only proves the *token* is good —
        // the user behind it may have been soft-deleted, locked out, or role-downgraded since
        // the refresh token was issued. We MUST re-read the current user row and fail-closed on
        // any liveness issue; otherwise a stolen or stale refresh token survives those signals
        // until natural token expiry.
        var result = await httpContext.AuthenticateAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        if (!result.Succeeded)
        {
            await TryLogLoginFailureAsync(auditService, logger, null, clientIp, httpContext.RequestAborted);
            return InvalidGrant("The refresh token is no longer valid.");
        }

        var subjectClaim = result.Principal!.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
        if (!Guid.TryParse(subjectClaim, out var userId))
        {
            await TryLogLoginFailureAsync(auditService, logger, null, clientIp, httpContext.RequestAborted);
            return InvalidGrant("The refresh token is no longer valid.");
        }

        var user = await userManager.FindForRefreshAsync(userId, httpContext.RequestAborted);
        if (user is null)
        {
            await TryLogLoginFailureAsync(auditService, logger, null, clientIp, httpContext.RequestAborted);
            return InvalidGrant("The refresh token is no longer valid.");
        }

        await TryLogLoginSuccessAsync(auditService, logger, user.Id, user.TenantId, clientIp, httpContext.RequestAborted);

        // Rebuild claims from the fresh row so role changes, email updates, and tenant moves
        // propagate on the next refresh. Scopes are preserved from the incoming principal —
        // they were negotiated at the initial grant and are client/server contract, not
        // identity state.
        var principal = BuildPrincipal(user, result.Principal.GetScopes());
        return Results.SignIn(principal, properties: null, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static ClaimsPrincipal BuildPrincipal(
        Core.Domain.User user,
        ImmutableArray<string> requestedScopes)
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

    // Results.Forbid takes IList<string>? for authenticationSchemes — opposite shape from
    // ControllerBase.Forbid(authenticationSchemes: "scheme", ...) which takes a single string.
    // Miscalling compiles with one-element list, so this is the canonical pattern.
    private static IResult InvalidGrant(string description) => Results.Forbid(
        properties: new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] =
                OpenIddictConstants.Errors.InvalidGrant,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }),
        authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    /// <summary>
    /// Resolves the caller's IP address honouring the reverse-proxy deployment topology: real
    /// deployments sit behind nginx/traefik, which rewrites the socket peer address to its own
    /// upstream and passes the original client IP in <c>X-Forwarded-For</c>. Take the first
    /// hop — subsequent entries may be proxies between the client and our edge.
    /// </summary>
    private static string? GetClientIp(HttpContext httpContext)
    {
        var forwardedFor = httpContext.Request.Headers[StrgHeaderNames.XForwardedFor].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            var first = forwardedFor.Split(',', 2)[0].Trim();
            if (first.Length > 0)
            {
                return first;
            }
        }

        return httpContext.Connection.RemoteIpAddress?.ToString();
    }

    // Audit writes are best-effort — an outage of the audit store must not turn into an auth
    // outage. Swallow and log; the auth decision stands regardless.
    private static async Task TryLogLoginSuccessAsync(
        IAuditService auditService,
        ILogger logger,
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

    private static async Task TryLogLoginFailureAsync(
        IAuditService auditService,
        ILogger logger,
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

    /// <summary>
    /// Stable <see cref="ILogger{TCategoryName}"/> category so auth events group under a
    /// predictable name in Serilog output — mirrors the marker-type pattern used by
    /// <c>UserRegistrationEndpoints.RegisterUserRequestLogCategory</c>.
    /// </summary>
    public sealed class TokenEndpointsLogCategory;
}
