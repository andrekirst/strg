using Mediator;
using Strg.Application.Abstractions;
using Strg.Core;
using Strg.Core.Domain;

namespace Strg.Application.Features.Drives.SetDefault;

/// <summary>
/// Sets the calling user's per-user default drive within the current tenant. Upserts the
/// (TenantId, UserId) row in <see cref="UserDriveDefault"/>; never mutates
/// <see cref="Drive.IsDefault"/> (that flag is the tenant-wide bootstrap default and is
/// owned by Create / Update). Cross-tenant <c>DriveId</c> lookups throw
/// <see cref="Strg.Core.Exceptions.NotFoundException"/>.
/// </summary>
public sealed record SetDefaultDriveCommand(Guid DriveId)
    : ICommand<Result<Drive>>, ITenantScopedCommand, IAuditedCommand;
