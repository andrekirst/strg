using HotChocolate.Authorization;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Core.Exceptions;
using Strg.Core.Storage;
using Strg.GraphQL.Inputs.File;
using Strg.GraphQL.Payloads;
using Strg.GraphQL.Payloads.File;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.Mutations.Storage;

[ExtendObjectType<StorageMutations>]
public sealed class FileMutations
{
    [Authorize(Policy = "FilesWrite")]
    public async Task<CreateFolderPayload> CreateFolderAsync(
        CreateFolderInput input,
        [Service] StrgDbContext db,
        [GlobalState("tenantId")] Guid tenantId,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        StoragePath path;
        try
        {
            path = StoragePath.Parse(input.Path);
        }
        catch (StoragePathException ex)
        {
            return new CreateFolderPayload(null, [new UserError("INVALID_PATH", ex.Message, "path")]);
        }

        var driveExists = await db.Drives.AnyAsync(d => d.Id == input.DriveId && d.TenantId == tenantId, ct);
        if (!driveExists)
            return new CreateFolderPayload(null, [new UserError("NOT_FOUND", "Drive not found.", "driveId")]);

        var folder = new FileItem
        {
            TenantId = tenantId,
            DriveId = input.DriveId,
            Name = path.Value.Split('/').Last(s => s.Length > 0),
            Path = path.Value,
            IsDirectory = true,
            CreatedBy = userId
        };

        db.Files.Add(folder);
        await db.SaveChangesAsync(ct);
        return new CreateFolderPayload(folder, null);
    }

    [Authorize(Policy = "FilesWrite")]
    public async Task<DeleteFilePayload> DeleteFileAsync(
        DeleteFileInput input,
        [Service] StrgDbContext db,
        CancellationToken ct)
    {
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == input.Id, ct);
        if (file is null)
            return new DeleteFilePayload(null, [new UserError("NOT_FOUND", "File not found.", null)]);

        file.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return new DeleteFilePayload(file.Id, null);
    }

    [Authorize(Policy = "FilesWrite")]
    public async Task<MoveFilePayload> MoveFileAsync(
        MoveFileInput input,
        [Service] StrgDbContext db,
        CancellationToken ct)
    {
        StoragePath targetPath;
        try
        {
            targetPath = StoragePath.Parse(input.TargetPath);
        }
        catch (StoragePathException ex)
        {
            return new MoveFilePayload(null, [new UserError("INVALID_PATH", ex.Message, "targetPath")]);
        }

        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == input.Id, ct);
        if (file is null)
            return new MoveFilePayload(null, [new UserError("NOT_FOUND", "File not found.", null)]);

        file.Path = targetPath.Value;
        file.Name = targetPath.Value.Split('/').Last(s => s.Length > 0);

        await db.SaveChangesAsync(ct);
        return new MoveFilePayload(file, null);
    }

    [Authorize(Policy = "FilesWrite")]
    public async Task<CopyFilePayload> CopyFileAsync(
        CopyFileInput input,
        [Service] StrgDbContext db,
        [GlobalState("tenantId")] Guid tenantId,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        StoragePath targetPath;
        try
        {
            targetPath = StoragePath.Parse(input.TargetPath);
        }
        catch (StoragePathException ex)
        {
            return new CopyFilePayload(null, [new UserError("INVALID_PATH", ex.Message, "targetPath")]);
        }

        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == input.Id, ct);
        if (file is null)
            return new CopyFilePayload(null, [new UserError("NOT_FOUND", "File not found.", null)]);

        var copy = new FileItem
        {
            TenantId = tenantId,
            DriveId = input.TargetDriveId ?? file.DriveId,
            Name = targetPath.Value.Split('/').Last(s => s.Length > 0),
            Path = targetPath.Value,
            Size = file.Size,
            MimeType = file.MimeType,
            StorageKey = file.StorageKey,
            CreatedBy = userId
        };

        db.Files.Add(copy);
        await db.SaveChangesAsync(ct);
        return new CopyFilePayload(copy, null);
    }

    [Authorize(Policy = "FilesWrite")]
    public async Task<RenameFilePayload> RenameFileAsync(
        RenameFileInput input,
        [Service] StrgDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.NewName) || input.NewName.Contains('/'))
            return new RenameFilePayload(null, [new UserError("VALIDATION_ERROR", "Invalid file name.", "newName")]);

        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == input.Id, ct);
        if (file is null)
            return new RenameFilePayload(null, [new UserError("NOT_FOUND", "File not found.", null)]);

        file.Name = input.NewName;
        await db.SaveChangesAsync(ct);
        return new RenameFilePayload(file, null);
    }
}
