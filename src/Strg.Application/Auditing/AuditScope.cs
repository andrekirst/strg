using Strg.Core.Domain;

namespace Strg.Application.Auditing;

internal sealed class AuditScope(
    ITenantContext tenantContext,
    ICurrentUser currentUser) : IAuditScope
{
    private string? _action;
    private string? _resourceType;
    private Guid _resourceId;
    private string? _details;
    private Guid? _userIdOverride;

    public bool IsPopulated => _action is not null;

    public void Record(
        string action,
        string resourceType,
        Guid resourceId,
        string? details = null,
        Guid? userId = null)
    {
        if (IsPopulated)
        {
            // Most commands map 1:1 to a single audit row. Forbid silent overwrites so a future
            // multi-record need surfaces as a deliberate design decision rather than a last-write
            // winning silently.
            throw new InvalidOperationException(
                "IAuditScope.Record called twice in one request. Only one audit entry per command is supported today.");
        }

        _action = action;
        _resourceType = resourceType;
        _resourceId = resourceId;
        _details = details;
        _userIdOverride = userId;
    }

    public AuditEntry? BuildEntry()
    {
        if (!IsPopulated)
        {
            return null;
        }

        return new AuditEntry
        {
            TenantId = tenantContext.TenantId,
            UserId = _userIdOverride ?? currentUser.UserId,
            Action = _action!,
            ResourceType = _resourceType!,
            ResourceId = _resourceId,
            Details = _details,
        };
    }

    public void Reset()
    {
        _action = null;
        _resourceType = null;
        _resourceId = default;
        _details = null;
        _userIdOverride = null;
    }
}
