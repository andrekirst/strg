namespace Strg.Application.Abstractions;

/// <summary>
/// Marker for commands whose successful outcome must be written to the audit trail.
/// AuditBehavior performs the post-success write. Failure paths are not audited by default —
/// matching the TagService / UserManager pattern of treating audit-log outages as ops concerns
/// that must never cascade into availability outages on the primary operation.
/// </summary>
public interface IAuditedCommand;
