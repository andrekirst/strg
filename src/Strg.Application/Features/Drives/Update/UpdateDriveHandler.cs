using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Strg.Application.Abstractions;
using Strg.Core;
using Strg.Core.Auditing;
using Strg.Core.Domain;

namespace Strg.Application.Features.Drives.Update;

internal sealed class UpdateDriveHandler(
    IStrgDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IAuditService auditService,
    ILogger<UpdateDriveHandler> logger)
    : ICommandHandler<UpdateDriveCommand, Result<Drive>>
{
    public async ValueTask<Result<Drive>> Handle(UpdateDriveCommand command, CancellationToken cancellationToken)
    {
        var drive = await db.Drives.FirstOrDefaultAsync(d => d.Id == command.Id, cancellationToken)
            .ConfigureAwait(false);
        if (drive is null)
        {
            return Result<Drive>.Failure("NotFound", "Drive not found.");
        }

        var changes = new List<string>();
        if (command.Name is not null && !string.Equals(drive.Name, command.Name, StringComparison.Ordinal))
        {
            drive.Name = command.Name;
            changes.Add($"name={command.Name}");
        }
        if (command.IsDefault.HasValue && drive.IsDefault != command.IsDefault.Value)
        {
            drive.IsDefault = command.IsDefault.Value;
            changes.Add($"is_default={command.IsDefault.Value.ToString().ToLowerInvariant()}");
        }

        if (changes.Count == 0)
        {
            // No-op update. Skip SaveChanges and audit — nothing changed, so recording an
            // audit row would misrepresent the state transition.
            return Result<Drive>.Success(drive);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await SafeAuditAsync(
            AuditActions.DriveUpdated,
            drive.Id,
            string.Join("; ", changes),
            cancellationToken).ConfigureAwait(false);

        return Result<Drive>.Success(drive);
    }

    private async Task SafeAuditAsync(string action, Guid driveId, string details, CancellationToken cancellationToken)
    {
        try
        {
            await auditService.LogAsync(new AuditEntry
            {
                TenantId = tenantContext.TenantId,
                UserId = currentUser.UserId,
                Action = action,
                ResourceType = "Drive",
                ResourceId = driveId,
                Details = details,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                throw;
            }
            logger.LogWarning(ex, "UpdateDrive: audit write failed for drive {DriveId}; drive op succeeded", driveId);
        }
    }
}
