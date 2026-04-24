using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Strg.Application.Abstractions;
using Strg.Core;
using Strg.Core.Auditing;
using Strg.Core.Domain;

namespace Strg.Application.Features.Tags.UpdateTag;

/// <summary>
/// Updates a tag's Value / ValueType by id. The Key is immutable — rename semantics go through
/// RemoveTag + AddTag. Tenant scoping comes from the StrgDbContext global query filter so a
/// caller in tenant B cannot update a tenant-A tag (the row is invisible to the load).
/// </summary>
internal sealed class UpdateTagHandler(
    IStrgDbContext db,
    ITenantContext tenantContext,
    IAuditService auditService,
    ILogger<UpdateTagHandler> logger)
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

        await SafeAuditAsync(
            AuditActions.TagAssigned,
            tag.FileId,
            tag.UserId,
            $"key={tag.Key}; value_type={command.ValueType.ToString().ToLowerInvariant()}",
            cancellationToken).ConfigureAwait(false);

        return Result<Tag>.Success(tag);
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
                "UpdateTag: audit write failed for {Action} on file {FileId} by user {UserId}; tag op succeeded",
                action, fileId, userId);
        }
    }
}
