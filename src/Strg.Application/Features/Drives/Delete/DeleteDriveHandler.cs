using Mediator;
using Microsoft.EntityFrameworkCore;
using Strg.Application.Abstractions;
using Strg.Application.Auditing;
using Strg.Core;
using Strg.Core.Auditing;

namespace Strg.Application.Features.Drives.Delete;

internal sealed class DeleteDriveHandler(
    IStrgDbContext db,
    IAuditScope auditScope)
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

        auditScope.Record(
            AuditActions.DriveDeleted,
            AuditResourceTypes.Drive,
            drive.Id,
            details: $"name={drive.Name}");

        return Result<Guid>.Success(command.Id);
    }
}
