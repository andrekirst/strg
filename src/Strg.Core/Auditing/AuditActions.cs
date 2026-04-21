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

    /// <summary>
    /// A user-scoped tag was assigned (or its value replaced) on a file. ResourceType is
    /// <c>"FileItem"</c>, ResourceId is the file id, UserId + TenantId populate the structural
    /// columns on <see cref="Strg.Core.Domain.AuditEntry"/>, and Details carries the normalised
    /// <c>(key, value_type)</c> pair.
    ///
    /// <para><b>Value deliberately excluded from Details.</b> Tag.Value is user-controlled up
    /// to 255 chars and may carry secrets (API keys, tokens, PII). The audit trail captures the
    /// fact of assignment, not its payload — pair with a per-value tagging policy when admin-
    /// visible value inspection is legitimately needed (v0.2 tracker).</para>
    /// </summary>
    public const string TagAssigned = "tag.assigned";

    /// <summary>
    /// A user-scoped tag was removed from a file. ResourceType is <c>"FileItem"</c>, ResourceId
    /// is the file id, UserId + TenantId populate the structural columns, and Details carries
    /// <c>key=...</c> for single-key removes or <c>bulk=true; count=N</c> for <c>RemoveAllAsync</c>.
    /// Idempotent removes (no row matched) do NOT emit — the audit log reflects state changes,
    /// not operator intent.
    /// </summary>
    public const string TagRemoved = "tag.removed";
}
