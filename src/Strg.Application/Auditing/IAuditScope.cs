using Strg.Core.Auditing;
using Strg.Core.Domain;

namespace Strg.Application.Auditing;

/// <summary>
/// Per-request channel a handler uses to declare "on success, write this audit row." The
/// <see cref="Behaviors.AuditBehavior{TMessage,TResponse}"/> reads <see cref="IsPopulated"/>
/// after the handler returns; when the command is marked <see cref="Abstractions.IAuditedCommand"/>,
/// the scope is populated, AND the response is successful, the entry is persisted via
/// <see cref="IAuditService"/>. Handlers that short-circuit a success path (no-op update, bulk
/// remove with zero rows) simply never call <see cref="Record"/>.
/// </summary>
public interface IAuditScope
{
    bool IsPopulated { get; }

    /// <summary>
    /// Declares the audit entry the behavior should write on success. <paramref name="userId"/>
    /// defaults to <see cref="ICurrentUser.UserId"/> when null — override for the tag-owner
    /// pattern where the audit subject is the entity's owner rather than the caller.
    /// </summary>
    void Record(
        string action,
        string resourceType,
        Guid resourceId,
        string? details = null,
        Guid? userId = null);

    /// <summary>
    /// Returns the composed <see cref="AuditEntry"/> or null when <see cref="IsPopulated"/> is
    /// false. Fills <see cref="TenantedEntity.TenantId"/> from <see cref="ITenantContext"/> and
    /// <see cref="AuditEntry.UserId"/> from the override or <see cref="ICurrentUser"/>.
    /// </summary>
    AuditEntry? BuildEntry();

    /// <summary>
    /// Clears any recorded entry so the scope is ready for the next command. The scope is
    /// <c>Scoped</c> in DI, but a single DI scope can dispatch multiple commands (integration
    /// tests, long-lived hosted services). <see cref="Behaviors.AuditBehavior{TMessage,TResponse}"/>
    /// calls this in a <c>finally</c> after every command so the double-<see cref="Record"/>
    /// guard only fires for true within-command bugs, not for cross-command state bleed.
    /// </summary>
    void Reset();
}
