using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data;

/// <summary>
/// EF Core-backed <see cref="IFileVersionRepository"/>.
///
/// <para><see cref="FileVersion"/> inherits <see cref="Entity"/>, NOT <see cref="TenantedEntity"/>,
/// so the global tenant filter does NOT apply here. Tenant isolation is enforced transitively:
/// every FileVersion belongs to a FileItem, and FileItem carries the TenantId. Callers that
/// accept a <c>fileId</c> from untrusted input must first resolve it through <see cref="FileRepository.GetByIdAsync"/>
/// (which IS tenant-filtered) before calling into this repository — otherwise a caller in tenant A
/// could query version history for a file in tenant B by guessing the fileId.</para>
/// </summary>
public sealed class FileVersionRepository(StrgDbContext db) : IFileVersionRepository
{
    public Task<FileVersion?> GetAsync(
        Guid fileId,
        int versionNumber,
        CancellationToken cancellationToken = default)
        => db.FileVersions.FirstOrDefaultAsync(
            v => v.FileId == fileId && v.VersionNumber == versionNumber,
            cancellationToken);

    /// <summary>
    /// Lists every version for a file, newest first. Intentionally materializes into a list — the
    /// number of versions per file is bounded (retention policy + explicit version caps), so the
    /// memory pressure is predictable. If retention grows unbounded later, revisit this.
    /// </summary>
    public async Task<IReadOnlyList<FileVersion>> ListAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        return await db.FileVersions
            .Where(v => v.FileId == fileId)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task AddAsync(FileVersion version, CancellationToken cancellationToken = default)
    {
        db.FileVersions.Add(version);
        return Task.CompletedTask;
    }
}
