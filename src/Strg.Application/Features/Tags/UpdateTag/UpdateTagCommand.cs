using Mediator;
using Strg.Application.Abstractions;
using Strg.Core;
using Strg.Core.Domain;

namespace Strg.Application.Features.Tags.UpdateTag;

public sealed record UpdateTagCommand(Guid Id, string Value, TagValueType ValueType)
    : ICommand<Result<Tag>>, ITenantScopedCommand, IAuditedCommand;
