using Mediator;
using Strg.Application.Abstractions;
using Strg.Core;

namespace Strg.Application.Features.Tags.RemoveTag;

public sealed record RemoveTagCommand(Guid Id) : ICommand<Result<Guid>>, ITenantScopedCommand;
