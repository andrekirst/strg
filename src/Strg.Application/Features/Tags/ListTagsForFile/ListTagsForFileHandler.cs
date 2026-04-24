using Mediator;
using Strg.Core.Domain;

namespace Strg.Application.Features.Tags.ListTagsForFile;

internal sealed class ListTagsForFileHandler(ITagRepository tagRepository, ICurrentUser currentUser)
    : IQueryHandler<ListTagsForFileQuery, IReadOnlyList<Tag>>
{
    public ValueTask<IReadOnlyList<Tag>> Handle(ListTagsForFileQuery query, CancellationToken cancellationToken)
        => new(tagRepository.GetByFileAsync(query.FileId, currentUser.UserId, cancellationToken));
}
