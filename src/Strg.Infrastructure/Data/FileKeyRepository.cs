using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data;

/// <summary>
/// EF Core-backed <see cref="IFileKeyRepository"/>. Like <see cref="FileVersionRepository"/>, the
/// owning entity inherits <see cref="Entity"/> rather than <see cref="TenantedEntity"/>, so the
/// global tenant filter does not apply here. Tenant scoping is transitive: callers MUST resolve
/// the owning <see cref="FileVersion"/> through <see cref="FileRepository"/> /
/// <see cref="FileVersionRepository"/> (which ARE tenant-filtered) before touching this repo.
///
/// <para>Consistent with the repository pattern, <see cref="AddAsync"/> stages the entity but
/// does NOT commit. The upload service commits <see cref="FileKey"/> + <see cref="FileVersion"/>
/// in one <c>SaveChangesAsync</c> — atomicity is a service-layer concern.</para>
/// </summary>
public sealed class FileKeyRepository(StrgDbContext db) : IFileKeyRepository
{
    public Task<FileKey?> GetByFileVersionAsync(Guid fileVersionId, CancellationToken cancellationToken = default)
        => db.FileKeys.FirstOrDefaultAsync(k => k.FileVersionId == fileVersionId, cancellationToken);

    public Task AddAsync(FileKey fileKey, CancellationToken cancellationToken = default)
    {
        db.FileKeys.Add(fileKey);
        return Task.CompletedTask;
    }
}
