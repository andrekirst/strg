using Mediator;
using Strg.Application.Abstractions;
using Strg.Application.Auditing;
using Strg.Core;
using Strg.Core.Auditing;
using Strg.Core.Domain;
using Strg.Core.Exceptions;

namespace Strg.Application.Features.Tags.AddTag;

internal sealed class AddTagHandler(
    IStrgDbContext db,
    ITagRepository tagRepository,
    IFileRepository fileRepository,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IAuditScope auditScope)
    : ICommandHandler<AddTagCommand, Result<Tag>>
{
    public async ValueTask<Result<Tag>> Handle(AddTagCommand command, CancellationToken cancellationToken)
    {
        // Tenant-ownership guard. IFileRepository.GetByIdAsync routes through the global tenant
        // filter so foreign-tenant / soft-deleted / non-existent files all collapse to null, and
        // the NotFoundException is the only observable distinction from the caller's side. Absent
        // this guard, a caller guessing a foreign fileId with a non-colliding key could INSERT a
        // ghost Tag row; see the rationale on TagService's EnsureFileVisibleAsync for v0.2 FK plan.
        var file = await fileRepository.GetByIdAsync(command.FileId, cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            throw new NotFoundException($"File '{command.FileId}' not found.");
        }

        var normalizedKey = command.Key.ToLowerInvariant();
        var userId = currentUser.UserId;

        var tag = new Tag
        {
            TenantId = tenantContext.TenantId,
            FileId = command.FileId,
            UserId = userId,
            Key = normalizedKey,
            Value = command.Value,
            ValueType = command.ValueType,
        };

        await tagRepository.UpsertAsync(tag, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Re-query to return the canonical persisted entity. If UpsertAsync collapsed onto an
        // existing row, the ctor-assigned Id above is stale and would mislead callers.
        var persisted = await tagRepository
            .GetByKeyAsync(command.FileId, userId, normalizedKey, cancellationToken)
            .ConfigureAwait(false);
        if (persisted is null)
        {
            throw new InvalidOperationException(
                $"Tag ({command.FileId}, {userId}, {normalizedKey}) disappeared immediately after upsert.");
        }

        auditScope.Record(
            AuditActions.TagAssigned,
            AuditResourceTypes.FileItem,
            command.FileId,
            details: $"key={normalizedKey}; value_type={command.ValueType.ToString().ToLowerInvariant()}",
            userId: userId);

        return Result<Tag>.Success(persisted);
    }
}
