using Mediator;
using Microsoft.EntityFrameworkCore;
using Strg.Application.Abstractions;
using Strg.Application.Auditing;
using Strg.Core;
using Strg.Core.Auditing;
using Strg.Core.Domain;

namespace Strg.Application.Features.Drives.Update;

internal sealed class UpdateDriveHandler(
    IStrgDbContext db,
    IAuditScope auditScope)
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

        auditScope.Record(
            AuditActions.DriveUpdated,
            AuditResourceTypes.Drive,
            drive.Id,
            details: string.Join("; ", changes));

        return Result<Drive>.Success(drive);
    }
}
