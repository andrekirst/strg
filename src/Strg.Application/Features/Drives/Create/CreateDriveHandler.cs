using Mediator;
using Microsoft.EntityFrameworkCore;
using Strg.Application.Abstractions;
using Strg.Application.Auditing;
using Strg.Core;
using Strg.Core.Auditing;
using Strg.Core.Domain;
using Strg.Plugin.Abstractions.Storage;

namespace Strg.Application.Features.Drives.Create;

internal sealed class CreateDriveHandler(
    IStrgDbContext db,
    ITenantContext tenantContext,
    IStorageProviderRegistry providerRegistry,
    IAuditScope auditScope)
    : ICommandHandler<CreateDriveCommand, Result<Drive>>
{
    public async ValueTask<Result<Drive>> Handle(CreateDriveCommand command, CancellationToken cancellationToken)
    {
        if (!providerRegistry.IsRegistered(command.ProviderType))
        {
            return Result<Drive>.Failure(
                "InvalidProviderType",
                $"Unknown provider type: {command.ProviderType}");
        }

        var tenantId = tenantContext.TenantId;

        // ArchTest exception: uniqueness check must span soft-deleted rows so a deleted drive's
        // name remains reserved within the tenant. The global filter disables both TenantId and
        // IsDeleted scoping when we call IgnoreQueryFilters, so we re-apply the tenant predicate
        // inline below. This is the single legitimate IgnoreQueryFilters call in Strg.Application;
        // the ApplicationDoesNotBypassTenantFiltersTests arch test allow-lists this file path and
        // rejects the call anywhere else.
        var existing = await db.Drives.IgnoreQueryFilters()
            .AnyAsync(d => d.TenantId == tenantId && d.Name == command.Name, cancellationToken)
            .ConfigureAwait(false);
        if (existing)
        {
            return Result<Drive>.Failure("DuplicateName", $"Drive '{command.Name}' already exists.");
        }

        // First non-soft-deleted drive in the tenant becomes the tenant-wide default. The
        // AnyAsync probe runs through both the tenant filter (scopes to current tenant) and the
        // soft-delete filter (hides DeletedAt-set rows), so a tenant that soft-deleted all of its
        // drives "restarts" — the next create is treated as a first drive again. An admin who
        // wants explicit control passes IsDefault=true|false on the command and bypasses the
        // heuristic entirely.
        bool isDefault;
        if (command.IsDefault.HasValue)
        {
            isDefault = command.IsDefault.Value;
        }
        else
        {
            var tenantHasAnyDrive = await db.Drives
                .AnyAsync(_ => true, cancellationToken)
                .ConfigureAwait(false);
            isDefault = !tenantHasAnyDrive;
        }

        var drive = new Drive
        {
            TenantId = tenantId,
            Name = command.Name,
            ProviderType = command.ProviderType,
            ProviderConfig = command.ProviderConfigJson ?? "{}",
            EncryptionEnabled = command.EncryptionEnabled,
            IsDefault = isDefault,
        };

        db.Drives.Add(drive);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        auditScope.Record(
            AuditActions.DriveCreated,
            AuditResourceTypes.Drive,
            drive.Id,
            details: $"name={drive.Name}; provider={drive.ProviderType}; encrypted={drive.EncryptionEnabled.ToString().ToLowerInvariant()}");

        return Result<Drive>.Success(drive);
    }
}
