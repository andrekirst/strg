using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Strg.Application.Abstractions;
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
    ITenantContext tenantContext,
    IAuditService auditService,
    ILogger<RemoveTagHandler> logger)
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

        await SafeAuditAsync(
            AuditActions.TagRemoved,
            tag.FileId,
            tag.UserId,
            $"key={tag.Key}",
            cancellationToken).ConfigureAwait(false);

        return Result<Guid>.Success(command.Id);
    }

    private async Task SafeAuditAsync(
        string action, Guid fileId, Guid userId, string details, CancellationToken cancellationToken)
    {
        try
        {
            await auditService.LogAsync(new AuditEntry
            {
                TenantId = tenantContext.TenantId,
                UserId = userId,
                Action = action,
                ResourceType = "FileItem",
                ResourceId = fileId,
                Details = details,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                throw;
            }
            logger.LogWarning(
                ex,
                "RemoveTag: audit write failed for {Action} on file {FileId} by user {UserId}; tag op succeeded",
                action, fileId, userId);
        }
    }
}
