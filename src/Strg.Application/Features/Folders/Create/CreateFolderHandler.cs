using Mediator;
using Microsoft.EntityFrameworkCore;
using Strg.Application.Abstractions;
using Strg.Application.Auditing;
using Strg.Core;
using Strg.Core.Auditing;
using Strg.Core.Domain;
using Strg.Plugin.Abstractions.Storage;

namespace Strg.Application.Features.Folders.Create;

/// <summary>
/// Creates a directory <see cref="FileItem"/> at <see cref="CreateFolderCommand.Path"/>, walking
/// each parent segment and materializing missing ones. STRG-042 introduced the auto-create-parents
/// loop (the prior handler only wrote the leaf row); the parent-walk + ParentId-chain is now the
/// authoritative create-folder path for both REST <c>POST /folders</c> and the GraphQL
/// <c>createFolder</c> mutation.
///
/// <para><b>Per-segment commit, not batched.</b> Each newly-materialized segment is flushed via
/// its own <see cref="IStrgDbContext.SaveChangesAsync"/>. The reason is NOT id materialization
/// (<see cref="Entity.Id"/> is client-allocated via <c>Guid.NewGuid()</c> at construction, so a
/// batched insert would already have ids available). The real reason is partial-progress
/// convergence: if walk-step N collides with an existing FILE at that path, walk-steps 0..N-1 are
/// already committed and a retry of the same request from any caller will discover them as
/// directories and resume from segment N — re-converging on the same Conflict outcome. A batched
/// insert would either all-or-nothing the entire walk (rolling back valid prefix work on a
/// later-segment conflict) or require ad-hoc rollback bookkeeping; per-segment commit is the
/// simpler invariant.</para>
///
/// <para><b>Tenant + soft-delete via global filter.</b> Every <see cref="IFileRepository.GetByPathAsync"/>
/// call here goes through the standard query-filter pipeline — cross-tenant rows and soft-deleted
/// rows are invisible. The unique index on <c>(DriveId, Path)</c> in
/// <c>FileItemConfiguration</c> guards against same-tenant duplicate inserts; concurrent-create
/// races on identical segments produce a <c>DbUpdateException</c> that surfaces as a 500 — out of
/// scope for STRG-042 (the issue spec does not address concurrent creates).</para>
///
/// <para><b>Audit semantics.</b> One <see cref="IAuditScope.Record"/> call per request, with the
/// leaf folder as <c>resourceId</c> and the actually-created paths in <c>details</c>. On full
/// idempotent re-entry (every segment already exists as a directory), no new rows are written
/// and <see cref="IAuditScope.Record"/> is intentionally NOT called — matching
/// <see cref="IAuditScope"/>'s "handlers that short-circuit a success path simply never call
/// Record" contract. The single-Record constraint
/// (<see cref="AuditScope"/> throws on second call) rules out the per-segment audit alternative.</para>
///
/// <para><b>Path normalization.</b> <see cref="StoragePath.Parse"/> is the single point of path
/// validation — traversal (<c>..</c>, leading <c>/</c>, <c>//</c>), reserved Windows names,
/// null-byte smuggling — and produces the canonical normalized form (no leading/trailing
/// slashes). The normalized <c>Value</c> is split on <c>/</c> to recover segments because
/// <see cref="StoragePath"/> currently has no public Segments accessor; the empty-segment filter
/// in the split ignores the inner-empty-segment case which Parse already rejects (<c>//</c> →
/// <c>StoragePathException</c>).</para>
/// </summary>
internal sealed class CreateFolderHandler(
    IStrgDbContext db,
    IFileRepository fileRepository,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IAuditScope auditScope)
    : ICommandHandler<CreateFolderCommand, Result<FileItem>>
{
    private const string InvalidPathCode = "InvalidPath";
    private const string NotFoundCode = "NotFound";
    private const string ConflictCode = "Conflict";

    public async ValueTask<Result<FileItem>> Handle(CreateFolderCommand command, CancellationToken cancellationToken)
    {
        StoragePath parsed;
        try
        {
            parsed = StoragePath.Parse(command.Path);
        }
        catch (StoragePathException ex)
        {
            return Result<FileItem>.Failure(InvalidPathCode, ex.Message);
        }

        var driveExists = await db.Drives
            .AnyAsync(d => d.Id == command.DriveId, cancellationToken)
            .ConfigureAwait(false);
        if (!driveExists)
        {
            return Result<FileItem>.Failure(NotFoundCode, "Drive not found.");
        }

        var segments = parsed.Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var createdPaths = new List<string>();
        FileItem? current = null;
        var currentPath = string.Empty;

        foreach (var segment in segments)
        {
            currentPath = currentPath.Length == 0 ? segment : $"{currentPath}/{segment}";

            var existing = await fileRepository
                .GetByPathAsync(command.DriveId, currentPath, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                if (!existing.IsDirectory)
                {
                    return Result<FileItem>.Failure(
                        ConflictCode,
                        $"Path segment '{currentPath}' exists as a file.");
                }
                current = existing;
                continue;
            }

            var folder = new FileItem
            {
                TenantId = tenantContext.TenantId,
                DriveId = command.DriveId,
                ParentId = current?.Id,
                Name = segment,
                Path = currentPath,
                IsDirectory = true,
                Size = 0,
                MimeType = "inode/directory",
                VersionCount = 0,
                CreatedBy = currentUser.UserId,
            };

            db.Files.Add(folder);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            createdPaths.Add(currentPath);
            current = folder;
        }

        if (createdPaths.Count > 0)
        {
            auditScope.Record(
                AuditActions.FolderCreated,
                AuditResourceTypes.FileItem,
                current!.Id,
                details: $"driveId={command.DriveId}; path={current.Path}; createdPaths=[{string.Join(", ", createdPaths)}]");
        }

        return Result<FileItem>.Success(current!);
    }
}
