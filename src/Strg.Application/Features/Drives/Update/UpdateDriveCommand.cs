using Mediator;
using Strg.Application.Abstractions;
using Strg.Core;
using Strg.Core.Domain;

namespace Strg.Application.Features.Drives.Update;

public sealed record UpdateDriveCommand(Guid Id, string? Name, bool? IsDefault)
    : ICommand<Result<Drive>>, ITenantScopedCommand;
