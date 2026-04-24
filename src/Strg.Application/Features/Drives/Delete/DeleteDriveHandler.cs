using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Strg.Application.Abstractions;
using Strg.Core;
using Strg.Core.Auditing;
using Strg.Core.Domain;

namespace Strg.Application.Features.Drives.Delete;

internal sealed class DeleteDriveHandler(
    IStrgDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IAuditService auditService,
    ILogger<DeleteDriveHandler> logger)
    : ICommandHandler<DeleteDriveCommand, Result<Guid>>
{
    public async ValueTask<Result<Guid>> Handle(DeleteDriveCommand command, CancellationToken cancellationToken)
    {
        var drive = await db.Drives.FirstOrDefaultAsync(d => d.Id == command.Id, cancellationToken)
            .ConfigureAwait(false);
        if (drive is null)
        {
            return Result<Guid>.Failure("NotFound", "Drive not found.");
        }

        drive.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await SafeAuditAsync(
            AuditActions.DriveDeleted,
            drive.Id,
            $"name={drive.Name}",
            cancellationToken).ConfigureAwait(false);

        return Result<Guid>.Success(command.Id);
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
            logger.LogWarning(ex, "DeleteDrive: audit write failed for drive {DriveId}; drive op succeeded", driveId);
        }
    }
}
