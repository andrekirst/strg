using Mediator;
using Microsoft.EntityFrameworkCore;
using Strg.Application.Abstractions;
using Strg.Application.Auditing;
using Strg.Core;
using Strg.Core.Auditing;
using Strg.Core.Domain;
using Strg.Core.Exceptions;

namespace Strg.Application.Features.Drives.SetDefault;

internal sealed class SetDefaultDriveHandler(
    IStrgDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IAuditScope auditScope)
    : ICommandHandler<SetDefaultDriveCommand, Result<Drive>>
{
    public async ValueTask<Result<Drive>> Handle(SetDefaultDriveCommand command, CancellationToken cancellationToken)
    {
        // Tenant filter on db.Drives ensures cross-tenant ids return null. Throwing NotFound
        // (rather than Result.Failure) matches CLAUDE.md's exception-error-mode rule: cross-tenant
        // access attempts are exceptional, not part of the wire contract. StrgErrorFilter maps it
        // to NOT_FOUND on the GraphQL surface.
        var drive = await db.Drives
            .FirstOrDefaultAsync(d => d.Id == command.DriveId, cancellationToken)
            .ConfigureAwait(false);
        if (drive is null)
        {
            throw new NotFoundException($"Drive '{command.DriveId}' not found.");
        }

        var tenantId = tenantContext.TenantId;
        var userId = currentUser.UserId;

        var existing = await db.UserDriveDefaults
            .FirstOrDefaultAsync(d => d.UserId == userId, cancellationToken)
            .ConfigureAwait(false);

        Guid? previousDriveId = existing?.DriveId;
        if (existing is null)
        {
            db.UserDriveDefaults.Add(new UserDriveDefault
            {
                TenantId = tenantId,
                UserId = userId,
                DriveId = drive.Id,
            });
        }
        else if (existing.DriveId != drive.Id)
        {
            existing.DriveId = drive.Id;
        }
        else
        {
            // No-op: user is "setting" the same drive they already have. Skip SaveChanges and
            // audit so the audit log reflects state changes only, mirroring UpdateDriveHandler.
            return Result<Drive>.Success(drive);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var details = previousDriveId.HasValue
            ? $"previous={previousDriveId.Value}"
            : "previous=none";
        auditScope.Record(
            AuditActions.DriveDefaultChanged,
            AuditResourceTypes.Drive,
            drive.Id,
            details: details,
            userId: userId);

        return Result<Drive>.Success(drive);
    }
}
