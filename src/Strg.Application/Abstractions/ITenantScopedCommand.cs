namespace Strg.Application.Abstractions;

/// <summary>
/// Marker for commands that operate on tenant-scoped data. TenantScopeBehavior enforces that
/// <see cref="Strg.Core.Domain.ITenantContext.TenantId"/> is non-empty before the handler runs.
/// Pre-auth commands that need to execute before a tenant is bound (Login, Register) deliberately
/// do NOT implement this marker.
/// </summary>
public interface ITenantScopedCommand;
