using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Strg.Application.Abstractions;
using Strg.Core;
using Strg.Core.Auditing;
using Strg.Core.Domain;
using Strg.Core.Storage;

namespace Strg.Application.Features.Folders.Create;

internal sealed class CreateFolderHandler(
    IStrgDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IAuditService auditService,
    ILogger<CreateFolderHandler> logger)
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

        await SafeAuditAsync(
            AuditActions.FolderCreated,
            folder.Id,
            $"driveId={command.DriveId}; path={path.Value}",
            cancellationToken).ConfigureAwait(false);

        return Result<FileItem>.Success(folder);
    }

    private async Task SafeAuditAsync(string action, Guid folderId, string details, CancellationToken cancellationToken)
    {
        try
        {
            await auditService.LogAsync(new AuditEntry
            {
                TenantId = tenantContext.TenantId,
                UserId = currentUser.UserId,
                Action = action,
                ResourceType = "FileItem",
                ResourceId = folderId,
                Details = details,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                throw;
            }
            logger.LogWarning(ex, "CreateFolder: audit write failed for folder {FolderId}; folder op succeeded", folderId);
        }
    }
}
