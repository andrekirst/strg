namespace Strg.Core.Auditing;

/// <summary>
/// Canonical string values for <see cref="Strg.Core.Domain.AuditEntry.Action"/>. Centralized so
/// filters, reports, and alerting queries can reference a single source of truth rather than
/// duplicating magic strings. Convention: lowercase dotted-segment pairs of
/// <c>resource.verb</c> or <c>resource.outcome</c>.
/// </summary>
public static class AuditActions
{
    /// <summary>
    /// Successful password-grant or refresh-token-grant token issuance. UserId + TenantId are
    /// populated from the authenticated user.
    /// </summary>
    public const string LoginSuccess = "login.success";

    /// <summary>
    /// Failed token-endpoint exchange — wrong password, unknown email, locked account, or a
    /// refresh token whose user is missing/deleted/locked. UserId and TenantId are
    /// <see cref="System.Guid.Empty"/> to avoid revealing whether a given email or subject exists.
    /// </summary>
    public const string LoginFailure = "login.failure";
}
