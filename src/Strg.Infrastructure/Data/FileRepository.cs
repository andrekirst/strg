using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data;

/// <summary>
/// EF Core-backed <see cref="IFileRepository"/>. All reads flow through the global query filters
/// declared in <see cref="StrgDbContext"/>, so this class NEVER calls
/// <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}(IQueryable{TEntity})"/> —
/// tenant isolation and soft-delete exclusion are non-negotiable on the file surface. Compare
/// <see cref="DriveRepository.GetByNameAsync"/>, which does bypass filters for a legitimate
/// uniqueness carve-out; there's no analogous need here (callers either ask by id, or by
/// (driveId, path) inside the caller's own tenant).
///
/// <para>Per CLAUDE.md the repository never commits. The spec example in STRG-033.md shows a
/// <c>SaveChangesAsync</c> method on the repo, but the spec's own Code Review Checklist
/// contradicts that and CLAUDE.md is authoritative — the caller owns the transaction.</para>
/// </summary>
public sealed class FileRepository(StrgDbContext db) : IFileRepository
{
    public Task<FileItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => db.Files.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

    public Task<FileItem?> GetByPathAsync(Guid driveId, string path, CancellationToken cancellationToken = default)
        => db.Files.FirstOrDefaultAsync(f => f.DriveId == driveId && f.Path == path, cancellationToken);

    /// <summary>
    /// Returns the immediate children of <paramref name="parentId"/> inside the drive. Passing
    /// <c>null</c> for <paramref name="parentId"/> lists the drive's root items — folder-browser
    /// UIs need this for the top-level view.
    /// </summary>
    public async Task<IReadOnlyList<FileItem>> ListByParentAsync(
        Guid driveId,
        Guid? parentId,
        CancellationToken cancellationToken = default)
    {
        return await db.Files
            .Where(f => f.DriveId == driveId && f.ParentId == parentId)
            .OrderBy(f => f.IsDirectory ? 0 : 1) // Folders first, then files — matches common UX.
            .ThenBy(f => f.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task AddAsync(FileItem file, CancellationToken cancellationToken = default)
    {
        db.Files.Add(file);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(FileItem file, CancellationToken cancellationToken = default)
    {
        db.Files.Update(file);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Soft-deletes by setting <see cref="TenantedEntity.DeletedAt"/> — never issues a physical
    /// DELETE. The soft-delete filter on <see cref="StrgDbContext"/> will exclude the row from
    /// every subsequent query, which also means this lookup will miss a file that has already
    /// been soft-deleted (the call becomes a no-op, matching DriveRepository's behaviour).
    /// </summary>
    public async Task SoftDeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == id, cancellationToken).ConfigureAwait(false);
        file?.DeletedAt = DateTimeOffset.UtcNow;
    }
}
