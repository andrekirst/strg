using Mediator;
using Strg.Application.Abstractions;
using Strg.Application.Auditing;
using Strg.Core;
using Strg.Core.Auditing;
using Strg.Core.Domain;
using Strg.Core.Exceptions;

namespace Strg.Application.Features.Tags.RemoveAllTags;

/// <summary>
/// Hard-deletes every tag the current user owns on a file. Scoped to (FileId, currentUser) —
/// matches TagService.RemoveAllAsync canonical semantics. Silently fixes the previous GraphQL
/// quirk that set DeletedAt across ALL users' tags on the file without a user filter.
/// </summary>
internal sealed class RemoveAllTagsHandler(
    IStrgDbContext db,
    ITagRepository tagRepository,
    IFileRepository fileRepository,
    ICurrentUser currentUser,
    IAuditScope auditScope)
    : ICommandHandler<RemoveAllTagsCommand, Result<int>>
{
    public async ValueTask<Result<int>> Handle(RemoveAllTagsCommand command, CancellationToken cancellationToken)
    {
        var file = await fileRepository.GetByIdAsync(command.FileId, cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            throw new NotFoundException($"File '{command.FileId}' not found.");
        }

        var userId = currentUser.UserId;
        var existing = await tagRepository.GetByFileAsync(command.FileId, userId, cancellationToken).ConfigureAwait(false);
        if (existing.Count == 0)
        {
            return Result<int>.Success(0);
        }

        await tagRepository.RemoveAllAsync(command.FileId, userId, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        auditScope.Record(
            AuditActions.TagRemoved,
            AuditResourceTypes.FileItem,
            command.FileId,
            details: $"bulk=true; count={existing.Count}",
            userId: userId);

        return Result<int>.Success(existing.Count);
    }
}
