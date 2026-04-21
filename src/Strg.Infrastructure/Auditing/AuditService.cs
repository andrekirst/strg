using Strg.Core.Auditing;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.Infrastructure.Auditing;

/// <summary>
/// EF Core-backed implementation that writes each entry with its own <c>SaveChangesAsync</c> —
/// auth audit records must be durable per-event, not piggy-backed on the caller's unit of work.
/// A failed sign-in whose audit row rolls back with the failure path would be silently invisible.
/// </summary>
public sealed class AuditService(StrgDbContext db) : IAuditService
{
    private const string UserResource = "User";

    public Task LogLoginSuccessAsync(
        Guid userId,
        Guid tenantId,
        string? clientIp,
        CancellationToken cancellationToken = default)
    {
        var entry = new AuditEntry
        {
            TenantId = tenantId,
            UserId = userId,
            Action = AuditActions.LoginSuccess,
            ResourceType = UserResource,
            ResourceId = userId,
            Details = FormatDetails(clientIp),
        };
        return LogAsync(entry, cancellationToken);
    }

    public Task LogLoginFailureAsync(
        string? email,
        string? clientIp,
        CancellationToken cancellationToken = default)
    {
        // UserId and TenantId stay Guid.Empty on purpose — see IAuditService.LogLoginFailureAsync
        // xmldoc. Resolving either from the submitted email would reopen the timing oracle
        // ValidateCredentialsAsync exists to close.
        var entry = new AuditEntry
        {
            TenantId = Guid.Empty,
            UserId = Guid.Empty,
            Action = AuditActions.LoginFailure,
            ResourceType = UserResource,
            ResourceId = null,
            Details = FormatDetails(clientIp, email),
        };
        return LogAsync(entry, cancellationToken);
    }

    public async Task LogAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        db.AuditEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string FormatDetails(string? clientIp, string? email = null)
    {
        // Plain "k=v; k=v" format — easy to grep, and Serilog structured logging on the caller
        // side already captures the same values as first-class properties for ops queries.
        var ip = string.IsNullOrWhiteSpace(clientIp) ? "unknown" : clientIp;
        return email is null
            ? $"ip={ip}"
            : $"email={email}; ip={ip}";
    }
}
