using Mediator;
using Microsoft.EntityFrameworkCore;
using Strg.Application.Abstractions;
using Strg.Application.Auditing;
using Strg.Core;
using Strg.Core.Auditing;
using Strg.Core.Domain;

namespace Strg.Application.Features.Tags.UpdateTag;

/// <summary>
/// Updates a tag's Value / ValueType by id. The Key is immutable — rename semantics go through
/// RemoveTag + AddTag. Tenant scoping comes from the StrgDbContext global query filter so a
/// caller in tenant B cannot update a tenant-A tag (the row is invisible to the load).
/// </summary>
internal sealed class UpdateTagHandler(IStrgDbContext db, IAuditScope auditScope)
    : ICommandHandler<UpdateTagCommand, Result<Tag>>
{
    public async ValueTask<Result<Tag>> Handle(UpdateTagCommand command, CancellationToken cancellationToken)
    {
        var tag = await db.Tags.FirstOrDefaultAsync(t => t.Id == command.Id, cancellationToken).ConfigureAwait(false);
        if (tag is null)
        {
            return Result<Tag>.Failure("NotFound", "Tag not found.");
        }

        tag.Value = command.Value;
        tag.ValueType = command.ValueType;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Audit against the tag's owner, not the caller. Tags are a user-scoped overlay; the
        // audit row is about the metadata-owner's state, not who triggered the mutation.
        auditScope.Record(
            AuditActions.TagAssigned,
            AuditResourceTypes.FileItem,
            tag.FileId,
            details: $"key={tag.Key}; value_type={command.ValueType.ToString().ToLowerInvariant()}",
            userId: tag.UserId);

        return Result<Tag>.Success(tag);
    }
}
