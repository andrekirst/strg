using System.Text.RegularExpressions;
using HotChocolate.Authorization;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.GraphQl.Inputs.Drive;
using Strg.GraphQl.Payloads;
using Strg.GraphQl.Payloads.Drive;
using Strg.Infrastructure.Data;

namespace Strg.GraphQl.Mutations.Storage;

[ExtendObjectType<StorageMutations>]
public sealed class DriveMutations
{
    private static readonly Regex ValidDriveName = new(@"^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);

    [Authorize(Policy = "Admin")]
    public async Task<CreateDrivePayload> CreateDriveAsync(
        CreateDriveInput input,
        [Service] StrgDbContext db,
        [GlobalState("tenantId")] Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (!ValidDriveName.IsMatch(input.Name))
        {
            return new CreateDrivePayload(null,
                [new UserError("VALIDATION_ERROR", "Drive name must match [a-z0-9-], max 64 chars.", "name")]);
        }

        // Reject oversized ProviderConfig before it hits the DB — cheaper error, no tx rollback.
        // DB column is capped at varchar(8192) as defense-in-depth backstop.
        if (input.ProviderConfig.Length > 8192)
        {
            return new CreateDrivePayload(null,
                [new UserError("VALIDATION_ERROR", "ProviderConfig JSON cannot exceed 8192 characters.", "providerConfig")]);
        }

        if (await db.Drives.AnyAsync(d => d.TenantId == tenantId && d.Name == input.Name, cancellationToken))
        {
            return new CreateDrivePayload(null,
                [new UserError("DUPLICATE_DRIVE_NAME", $"Drive '{input.Name}' already exists.", "name")]);
        }

        var drive = new Drive
        {
            TenantId = tenantId,
            Name = input.Name,
            ProviderType = input.ProviderType,
            ProviderConfig = input.ProviderConfig,
            EncryptionEnabled = input.IsEncrypted ?? false,
            IsDefault = input.IsDefault ?? false
        };

        db.Drives.Add(drive);
        await db.SaveChangesAsync(cancellationToken);
        return new CreateDrivePayload(drive, null);
    }

    [Authorize(Policy = "Admin")]
    public async Task<UpdateDrivePayload> UpdateDriveAsync(
        UpdateDriveInput input,
        [Service] StrgDbContext db,
        [GlobalState("tenantId")] Guid tenantId,
        CancellationToken cancellationToken)
    {
        var drive = await db.Drives.FirstOrDefaultAsync(
            d => d.Id == input.Id && d.TenantId == tenantId, cancellationToken);

        if (drive is null)
        {
            return new UpdateDrivePayload(null,
                [new UserError("NOT_FOUND", "Drive not found.", null)]);
        }

        if (input.Name is not null)
        {
            if (!ValidDriveName.IsMatch(input.Name))
            {
                return new UpdateDrivePayload(null,
                    [new UserError("VALIDATION_ERROR", "Drive name must match [a-z0-9-].", "name")]);
            }

            drive.Name = input.Name;
        }
        if (input.IsDefault.HasValue)
        {
            drive.IsDefault = input.IsDefault.Value;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new UpdateDrivePayload(drive, null);
    }

    [Authorize(Policy = "Admin")]
    public async Task<DeleteDrivePayload> DeleteDriveAsync(
        DeleteDriveInput input,
        [Service] StrgDbContext db,
        [GlobalState("tenantId")] Guid tenantId,
        CancellationToken cancellationToken)
    {
        var drive = await db.Drives.FirstOrDefaultAsync(
            d => d.Id == input.Id && d.TenantId == tenantId, cancellationToken);

        if (drive is null)
        {
            return new DeleteDrivePayload(null,
                [new UserError("NOT_FOUND", "Drive not found.", null)]);
        }

        drive.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new DeleteDrivePayload(drive.Id, null);
    }
}
