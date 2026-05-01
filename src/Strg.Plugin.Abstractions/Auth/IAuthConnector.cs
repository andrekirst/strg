namespace Strg.Plugin.Abstractions.Auth;

/// <summary>
/// External identity-provider bridge — LDAP, SAML, OAuth2, or any future protocol. The host calls
/// the connector during sign-in when the local password store rejects (or is bypassed by tenant
/// configuration). Identity returned by <see cref="AuthenticateAsync"/> is a
/// <see cref="System.Security.Claims.ClaimsIdentity"/> (NOT a <c>ClaimsPrincipal</c>): the host
/// owns principal construction so it can validate, augment, and stamp tenant claims onto the
/// identity before issuing tokens. Plugins are deliberately denied that authority.
///
/// <para><b>Tenant scoping.</b> The host passes a username that has already been tenant-scoped;
/// the connector itself does not need to know about tenants — it authenticates against its IdP
/// and returns a flat identity. The host translates the connector's group names to tenant-local
/// roles via the tenant's auth-config table.</para>
/// </summary>
public interface IAuthConnector : IStrgPlugin
{
    /// <summary>
    /// Stable lowercase identifier used by the host registry to route a tenant's auth-config row
    /// to the right connector. Examples: <c>"ldap"</c>, <c>"saml"</c>, <c>"oauth2"</c>.
    /// </summary>
    string ConnectorType { get; }

    /// <summary>
    /// Authenticates <paramref name="username"/> + <paramref name="password"/> against the IdP.
    /// Credential rejection MUST be reported via <see cref="AuthConnectorResult.Success"/> ==
    /// <c>false</c> with a <see cref="AuthConnectorResult.ErrorMessage"/> rather than thrown —
    /// exceptions are reserved for transport / IdP-availability failures so the host can
    /// distinguish "wrong password" from "directory unreachable".
    /// </summary>
    Task<AuthConnectorResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the IdP-side group memberships for <paramref name="username"/> as flat names (not
    /// distinguished names or GUIDs). The host maps each name to a local role via the tenant's
    /// auth-config table.
    /// </summary>
    Task<IReadOnlyList<string>> GetGroupsAsync(
        string username,
        CancellationToken cancellationToken);
}
