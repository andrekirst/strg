using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using System.Net.Mime;
using System.Security.Claims;

namespace Strg.Api.Endpoints;

/// <summary>
/// OpenIddict userinfo-endpoint passthrough. Returns the subset of claims enabled for this
/// release (sub / email / name); additional claims are intentionally omitted until ID-token
/// shaping lands.
/// </summary>
public static class UserInfoEndpoints
{
    public static IEndpointRouteBuilder MapUserInfoEndpoints(this IEndpointRouteBuilder app)
    {
        // Dual GET/POST mirrors the [HttpGet]+[HttpPost] pair on the former UserInfoController.
        //
        // Authorization binds the OpenIddict SERVER scheme explicitly, NOT the validation
        // scheme that AddStrgOpenIddict sets as the default. The userinfo endpoint sits inside
        // the server-passthrough pipeline: OpenIddict authenticates the incoming access token
        // with its server-side handler and shapes the outgoing body. A bare RequireAuthorization
        // would fall through to the default scheme (validation) and fail closed on every call.
        app.MapMethods("/connect/userinfo", [HttpMethods.Get, HttpMethods.Post], GetUserInfo)
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            })
            .WithName("GetUserInfo")
            .WithTags("Auth")
            .Produces<Dictionary<string, object>>(StatusCodes.Status200OK, contentType: MediaTypeNames.Application.Json)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    private static Ok<Dictionary<string, object>> GetUserInfo(ClaimsPrincipal user)
    {
        var claims = new Dictionary<string, object>(StringComparer.Ordinal);

        var subject = user.FindFirstValue(OpenIddictConstants.Claims.Subject);
        if (subject is not null)
        {
            claims[OpenIddictConstants.Claims.Subject] = subject;
        }

        var email = user.FindFirstValue(OpenIddictConstants.Claims.Email);
        if (email is not null)
        {
            claims[OpenIddictConstants.Claims.Email] = email;
        }

        var name = user.FindFirstValue(OpenIddictConstants.Claims.Name);
        if (name is not null)
        {
            claims[OpenIddictConstants.Claims.Name] = name;
        }

        return TypedResults.Ok(claims);
    }
}
