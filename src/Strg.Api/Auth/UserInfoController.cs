using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using System.Security.Claims;

namespace Strg.Api.Auth;

[ApiController]
[Authorize(AuthenticationSchemes = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)]
public sealed class UserInfoController : ControllerBase
{
    [HttpGet("~/connect/userinfo")]
    [HttpPost("~/connect/userinfo")]
    [Produces("application/json")]
    public IActionResult GetUserInfo()
    {
        var claims = new Dictionary<string, object>(StringComparer.Ordinal);

        var subject = User.FindFirstValue(OpenIddictConstants.Claims.Subject);
        if (subject is not null)
            claims[OpenIddictConstants.Claims.Subject] = subject;

        var email = User.FindFirstValue(OpenIddictConstants.Claims.Email);
        if (email is not null)
            claims[OpenIddictConstants.Claims.Email] = email;

        var name = User.FindFirstValue(OpenIddictConstants.Claims.Name);
        if (name is not null)
            claims[OpenIddictConstants.Claims.Name] = name;

        return Ok(claims);
    }
}
