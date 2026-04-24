using Mediator;
using Strg.Application.Abstractions;
using Strg.Core;

namespace Strg.Application.Features.Drives.Delete;

public sealed record DeleteDriveCommand(Guid Id) : ICommand<Result<Guid>>, ITenantScopedCommand, IAuditedCommand;
