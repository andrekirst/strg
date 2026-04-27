using MassTransit;
using Mediator;
using Strg.Application.Abstractions;
using Strg.Core;
using Strg.Core.Domain;
using Strg.Core.Events;

namespace Strg.Application.Features.Files.Delete;

/// <summary>
/// Soft-deletes the file at <c>(DriveId, FileId)</c> and, when it is a directory, every
/// descendant under its path prefix in the same DB transaction. Then publishes
/// <see cref="FileDeletedEvent"/> via the MassTransit outbox so <c>AuditLogConsumer</c>
/// writes the corresponding audit row asynchronously.
///
/// <para><b>Already-deleted file → NotFound.</b> <see cref="IFileRepository.GetByIdAsync"/>
/// is wrapped by the global soft-delete query filter, so a re-delete observes a null here
/// and returns the same <c>NotFound</c> code as a never-existed file. The alternative —
/// idempotent 204 — would require bypassing that global filter on the lookup, which
/// collides with <c>FileRepository</c>'s "filters always apply" class invariant and would
/// also widen the surface to other tenants.</para>
///
/// <para><b>Cross-drive id mismatch → NotFound.</b> A caller addressing a known file id via
/// an unrelated drive id is collapsed to <c>NotFound</c> (NOT <c>Forbidden</c>) so the wire
/// shape cannot be used as an enumeration oracle for files in drives the caller cannot
/// access. Same security stance as <c>FileDownloadResolver</c>.</para>
///
/// <para><b>Outbox publish ordering.</b> <c>IPublishEndpoint.Publish</c> is called BEFORE
/// <see cref="IStrgDbContext.SaveChangesAsync"/>. MassTransit's <c>UseBusOutbox()</c>
/// interceptor stages the publish on the change tracker as an outbox row; the single
/// subsequent <c>SaveChangesAsync</c> commits the soft-delete columns and the outbox row
/// in one transaction. A post-save publish would either lose the message (no transaction)
/// or require a second <c>SaveChangesAsync</c> — reintroducing the dual-write race the
/// outbox exists to close. CLAUDE.md's "publish AFTER SaveChangesAsync" guidance pre-dates
/// the <c>UseBusOutbox()</c> wiring; cross-reference <c>UserManager.SetPasswordAsync</c>
/// and <c>StrgTusStore</c> for the canonical publish-before-save pattern.</para>
/// </summary>
internal sealed class DeleteFileHandler(
    IStrgDbContext db,
    IFileRepository fileRepository,
    IPublishEndpoint publishEndpoint,
    ITenantContext tenantContext,
    ICurrentUser currentUser)
    : ICommandHandler<DeleteFileCommand, Result>
{
    private const string NotFoundCode = "NotFound";

    public async ValueTask<Result> Handle(DeleteFileCommand command, CancellationToken cancellationToken)
    {
        var file = await fileRepository.GetByIdAsync(command.FileId, cancellationToken).ConfigureAwait(false);
        if (file is null || file.DriveId != command.DriveId)
        {
            return Result.Failure(NotFoundCode, "File not found.");
        }

        var now = DateTimeOffset.UtcNow;
        file.DeletedAt = now;

        if (file.IsDirectory)
        {
            // Anchor with a trailing '/' so StartsWith cannot match a sibling whose Path
            // begins with the same characters but a different next segment (e.g., the
            // directory "docs" must NOT match "docsbackup"). Mirrors the prefixSlash
            // construction in ListFilesHandler.ApplyPathFilter.
            var prefix = file.Path + "/";
            await foreach (var descendant in fileRepository
                .GetDescendantsAsync(command.DriveId, prefix, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                descendant.DeletedAt = now;
            }
        }

        // Staged on the change tracker by UseBusOutbox; flushed atomically with the entity
        // mutations by the single SaveChangesAsync below.
        await publishEndpoint.Publish(
            new FileDeletedEvent(
                tenantContext.TenantId,
                file.Id,
                file.DriveId,
                currentUser.UserId),
            cancellationToken).ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}
