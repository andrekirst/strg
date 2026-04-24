using Mediator;
using Strg.Application.Abstractions;
using Strg.Core;
using Strg.Core.Domain;

namespace Strg.Application.Features.Drives.Create;

public sealed record CreateDriveCommand(
    string Name,
    string ProviderType,
    string? ProviderConfigJson = null,
    bool EncryptionEnabled = false,
    bool? IsDefault = null)
    : ICommand<Result<Drive>>, ITenantScopedCommand, IAuditedCommand;
