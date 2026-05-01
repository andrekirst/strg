using System.Security.Claims;

namespace Strg.Plugin.Abstractions.Auth;

/// <summary>
/// Outcome of <see cref="IAuthConnector.AuthenticateAsync"/>. Three-arity invariant:
/// <see cref="Success"/> = <c>true</c> requires <see cref="Identity"/> to be non-null;
/// <see cref="Success"/> = <c>false</c> requires <see cref="ErrorMessage"/> to be non-null.
/// The <see cref="System.Security.Claims.ClaimsIdentity"/> (not <c>ClaimsPrincipal</c>) keeps
/// principal construction with the host — plugins do not get to mint security principals.
/// </summary>
public sealed record AuthConnectorResult(
    bool Success,
    string? ErrorMessage,
    ClaimsIdentity? Identity);
