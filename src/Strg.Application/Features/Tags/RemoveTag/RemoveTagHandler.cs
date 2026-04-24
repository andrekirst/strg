using Mediator;
using Microsoft.EntityFrameworkCore;
using Strg.Application.Abstractions;
using Strg.Application.Auditing;
using Strg.Core;
using Strg.Core.Auditing;
using Strg.Core.Domain;

namespace Strg.Application.Features.Tags.RemoveTag;

/// <summary>
/// Hard-deletes a tag by id via <see cref="ITagRepository.RemoveAsync"/>. Silently fixes the
/// previous GraphQL quirk that soft-deleted tags (setting <c>DeletedAt</c>) — tags are a
/// user-scoped metadata overlay, not a document that needs a recycle-bin workflow. Tenant scope
/// comes from the StrgDbContext global filter on the initial load.
/// </summary>
internal sealed class RemoveTagHandler(
    IStrgDbContext db,
    ITagRepository tagRepository,
    IAuditScope auditScope)
    : ICommandHandler<RemoveTagCommand, Result<Guid>>
{
    public async ValueTask<Result<Guid>> Handle(RemoveTagCommand command, CancellationToken cancellationToken)
    {
        var tag = await db.Tags.FirstOrDefaultAsync(t => t.Id == command.Id, cancellationToken).ConfigureAwait(false);
        if (tag is null)
        {
            return Result<Guid>.Failure("NotFound", "Tag not found.");
        }

        await tagRepository.RemoveAsync(tag.FileId, tag.UserId, tag.Key, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Audit against the tag's owner, not the caller — same rationale as UpdateTagHandler.
        auditScope.Record(
            AuditActions.TagRemoved,
            AuditResourceTypes.FileItem,
            tag.FileId,
            details: $"key={tag.Key}",
            userId: tag.UserId);

        return Result<Guid>.Success(command.Id);
    }
}
