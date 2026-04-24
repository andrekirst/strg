using Mediator;
using Microsoft.Extensions.Logging;
using Strg.Application.Abstractions;
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
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IAuditService auditService,
    ILogger<RemoveAllTagsHandler> logger)
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

        await SafeAuditAsync(
            AuditActions.TagRemoved,
            command.FileId,
            userId,
            $"bulk=true; count={existing.Count}",
            cancellationToken).ConfigureAwait(false);

        return Result<int>.Success(existing.Count);
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
                "RemoveAllTags: audit write failed for {Action} on file {FileId} by user {UserId}; tag op succeeded",
                action, fileId, userId);
        }
    }
}
