using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.Infrastructure.Thumbnails;

/// <summary>
/// EF Core-backed <see cref="IThumbnailRepository"/>. Per project convention, NO calls to
/// <c>SaveChangesAsync</c> — the consumer / endpoint handler owns the transaction boundary.
/// </summary>
public sealed class ThumbnailRepository(StrgDbContext db) : IThumbnailRepository
{
    public void Add(ThumbnailEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        db.ThumbnailEntries.Add(entry);
    }

    public Task<ThumbnailEntry?> GetAsync(
        Guid fileVersionId,
        string variant,
        string format,
        CancellationToken cancellationToken = default) =>
        db.ThumbnailEntries.FirstOrDefaultAsync(
            t => t.FileVersionId == fileVersionId
                 && t.Variant == variant
                 && t.Format == format,
            cancellationToken);

    public async Task<IReadOnlyList<ThumbnailEntry>> GetByFileVersionAsync(
        Guid fileVersionId,
        CancellationToken cancellationToken = default) =>
        await db.ThumbnailEntries
            .Where(t => t.FileVersionId == fileVersionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<ThumbnailEntry>> GetByFileAsync(
        Guid fileId,
        CancellationToken cancellationToken = default) =>
        await db.ThumbnailEntries
            .Where(t => t.FileId == fileId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public void SoftDeleteRange(IEnumerable<ThumbnailEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in entries)
        {
            entry.DeletedAt = now;
        }
    }
}
