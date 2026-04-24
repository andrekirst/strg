using Mediator;
using Strg.Application.Abstractions;
using Strg.Core;

namespace Strg.Application.Features.Tags.RemoveAllTags;

public sealed record RemoveAllTagsCommand(Guid FileId) : ICommand<Result<int>>, ITenantScopedCommand;
