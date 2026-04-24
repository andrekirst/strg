using Mediator;
using Strg.Application.Abstractions;
using Strg.Core;
using Strg.Core.Domain;

namespace Strg.Application.Features.Tags.AddTag;

public sealed record AddTagCommand(Guid FileId, string Key, string Value, TagValueType ValueType)
    : ICommand<Result<Tag>>, ITenantScopedCommand;
