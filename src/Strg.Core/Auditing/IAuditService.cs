using Strg.Core.Domain;

namespace Strg.Core.Auditing;

/// <summary>
/// Writes immutable audit-trail entries to the durable store. Every call SHOULD persist its
/// entry immediately rather than batching — an audit record that hangs in memory and is lost to
/// a crash defeats the purpose of the audit trail.
///
/// <para>
/// Callers in security-sensitive flows (auth, admin ops) MUST guard their invocation against
/// exceptions thrown by the implementation: an audit-store outage is a logging/ops concern, not
/// an authorization gate. Failing the user's primary operation because the audit row couldn't
/// be written would turn a monitoring problem into an availability problem.
/// </para>
/// </summary>
public interface IAuditService
{
    /// <summary>
    /// Records a successful token-endpoint exchange (password grant or refresh grant). Writes
    /// the entry under the user's own <paramref name="tenantId"/> so per-tenant admin queries
    /// surface the event without cross-tenant leakage.
    /// </summary>
    Task LogLoginSuccessAsync(Guid userId, Guid tenantId, string? clientIp, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failed token-endpoint exchange. <paramref name="email"/> is the raw value the
    /// client submitted (or <see langword="null"/> when no email was available — e.g. an invalid
    /// refresh token). UserId and TenantId are stored as <see cref="Guid.Empty"/> on purpose:
    /// resolving them from the email would reveal existence via wall-clock timing and defeat the
    /// anti-enumeration story established in <c>ValidateCredentialsAsync</c>.
    /// </summary>
    Task LogLoginFailureAsync(string? email, string? clientIp, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generic entry-point for callers outside the auth path (file ops, drive ops, admin
    /// mutations). The caller constructs the <see cref="AuditEntry"/> itself so the action
    /// vocabulary is not constrained to pre-declared helpers.
    /// </summary>
    Task LogAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}
