using Mediator;
using Microsoft.EntityFrameworkCore;
using Strg.Application.Abstractions;
using Strg.Application.Auditing;
using Strg.Core;
using Strg.Core.Auditing;
using Strg.Core.Domain;
using Strg.Core.Storage;

namespace Strg.Application.Features.Folders.Create;

internal sealed class CreateFolderHandler(
    IStrgDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IAuditScope auditScope)
    : ICommandHandler<CreateFolderCommand, Result<FileItem>>
{
    public async ValueTask<Result<FileItem>> Handle(CreateFolderCommand command, CancellationToken cancellationToken)
    {
        StoragePath path;
        try
        {
            path = StoragePath.Parse(command.Path);
        }
        catch (StoragePathException ex)
        {
            return Result<FileItem>.Failure("InvalidPath", ex.Message);
        }

        var driveExists = await db.Drives
            .AnyAsync(d => d.Id == command.DriveId, cancellationToken)
            .ConfigureAwait(false);
        if (!driveExists)
        {
            return Result<FileItem>.Failure("NotFound", "Drive not found.");
        }

        var folder = new FileItem
        {
            TenantId = tenantContext.TenantId,
            DriveId = command.DriveId,
            Name = path.Value.Split('/').Last(s => s.Length > 0),
            Path = path.Value,
            IsDirectory = true,
            CreatedBy = currentUser.UserId,
        };

        db.Files.Add(folder);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        auditScope.Record(
            AuditActions.FolderCreated,
            AuditResourceTypes.FileItem,
            folder.Id,
            details: $"driveId={command.DriveId}; path={path.Value}");

        return Result<FileItem>.Success(folder);
    }
}
