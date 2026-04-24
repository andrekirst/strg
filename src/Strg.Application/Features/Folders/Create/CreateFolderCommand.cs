using Mediator;
using Strg.Application.Abstractions;
using Strg.Core;
using Strg.Core.Domain;

namespace Strg.Application.Features.Folders.Create;

public sealed record CreateFolderCommand(Guid DriveId, string Path)
    : ICommand<Result<FileItem>>, ITenantScopedCommand, IAuditedCommand;
