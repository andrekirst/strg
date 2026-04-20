using System.Text.RegularExpressions;
using HotChocolate.Authorization;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.GraphQL.Inputs.Drive;
using Strg.GraphQL.Payloads;
using Strg.GraphQL.Payloads.Drive;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.Mutations.Storage;

[ExtendObjectType<StorageMutations>]
public sealed class DriveMutations
{
    private static readonly Regex ValidDriveName = new(@"^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);

    [Authorize(Policy = "Admin")]
    public async Task<CreateDrivePayload> CreateDriveAsync(
        CreateDriveInput input,
        [Service] StrgDbContext db,
        [GlobalState("tenantId")] Guid tenantId,
        CancellationToken ct)
    {
        if (!ValidDriveName.IsMatch(input.Name))
            return new CreateDrivePayload(null,
                [new UserError("VALIDATION_ERROR", "Drive name must match [a-z0-9-], max 64 chars.", "name")]);

        if (await db.Drives.AnyAsync(d => d.TenantId == tenantId && d.Name == input.Name, ct))
            return new CreateDrivePayload(null,
                [new UserError("DUPLICATE_DRIVE_NAME", $"Drive '{input.Name}' already exists.", "name")]);

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
        await db.SaveChangesAsync(ct);
        return new CreateDrivePayload(drive, null);
    }

    [Authorize(Policy = "Admin")]
    public async Task<UpdateDrivePayload> UpdateDriveAsync(
        UpdateDriveInput input,
        [Service] StrgDbContext db,
        [GlobalState("tenantId")] Guid tenantId,
        CancellationToken ct)
    {
        var drive = await db.Drives.FirstOrDefaultAsync(
            d => d.Id == input.Id && d.TenantId == tenantId, ct);

        if (drive is null)
            return new UpdateDrivePayload(null,
                [new UserError("NOT_FOUND", "Drive not found.", null)]);

        if (input.Name is not null)
        {
            if (!ValidDriveName.IsMatch(input.Name))
                return new UpdateDrivePayload(null,
                    [new UserError("VALIDATION_ERROR", "Drive name must match [a-z0-9-].", "name")]);
            drive.Name = input.Name;
        }
        if (input.IsDefault.HasValue) drive.IsDefault = input.IsDefault.Value;

        await db.SaveChangesAsync(ct);
        return new UpdateDrivePayload(drive, null);
    }

    [Authorize(Policy = "Admin")]
    public async Task<DeleteDrivePayload> DeleteDriveAsync(
        DeleteDriveInput input,
        [Service] StrgDbContext db,
        [GlobalState("tenantId")] Guid tenantId,
        CancellationToken ct)
    {
        var drive = await db.Drives.FirstOrDefaultAsync(
            d => d.Id == input.Id && d.TenantId == tenantId, ct);

        if (drive is null)
            return new DeleteDrivePayload(null,
                [new UserError("NOT_FOUND", "Drive not found.", null)]);

        drive.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return new DeleteDrivePayload(drive.Id, null);
    }
}
