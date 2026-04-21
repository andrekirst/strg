using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data;

/// <summary>
/// EF Core-backed <see cref="ITagRepository"/>. <see cref="Tag"/> inherits
/// <see cref="TenantedEntity"/>, so the global tenant filter applies to every query without
/// explicit <c>TenantId</c> predicates.
///
/// <para>Per CLAUDE.md the repository never commits — callers (the <c>TagService</c>, in
/// practice) own the transaction and invoke <c>SaveChangesAsync</c> themselves. The repo
/// mutates <see cref="DbSet{Tag}"/> state only.</para>
///
/// <para>Key comparison relies on <see cref="Tag.Key"/>'s init-setter lowercasing keys on
/// assignment (see STRG-046). Repository methods that accept a <c>key</c> parameter normalize
/// it to lowercase before building the WHERE clause so callers can pass keys in any case.</para>
/// </summary>
public sealed class TagRepository(StrgDbContext db) : ITagRepository
{
    public async Task<IReadOnlyList<Tag>> GetByFileAsync(
        Guid fileId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await db.Tags
            .Where(t => t.FileId == fileId && t.UserId == userId)
            .OrderBy(t => t.Key)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<Tag?> GetByKeyAsync(
        Guid fileId,
        Guid userId,
        string key,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = key.ToLowerInvariant();
        return db.Tags.FirstOrDefaultAsync(
            t => t.FileId == fileId && t.UserId == userId && t.Key == normalizedKey,
            cancellationToken);
    }

    /// <summary>
    /// If a tag with the same <c>(FileId, UserId, Key)</c> already exists, its <see cref="Tag.Value"/>
    /// and <see cref="Tag.ValueType"/> are copied from <paramref name="tag"/> onto the existing
    /// tracked entity. Otherwise <paramref name="tag"/> itself is added. The caller must invoke
    /// <c>SaveChangesAsync</c> on the DbContext to persist — this method only mutates change-tracker
    /// state.
    /// </summary>
    public async Task UpsertAsync(Tag tag, CancellationToken cancellationToken = default)
    {
        // Tag.Key is normalized in the setter, so `tag.Key` here is already lowercase.
        var existing = await db.Tags
            .FirstOrDefaultAsync(
                t => t.FileId == tag.FileId && t.UserId == tag.UserId && t.Key == tag.Key,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.Tags.Add(tag);
        }
        else
        {
            existing.Value = tag.Value;
            existing.ValueType = tag.ValueType;
        }
    }

    public async Task RemoveAsync(
        Guid fileId,
        Guid userId,
        string key,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = key.ToLowerInvariant();
        var tag = await db.Tags
            .FirstOrDefaultAsync(
                t => t.FileId == fileId && t.UserId == userId && t.Key == normalizedKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (tag is not null)
        {
            db.Tags.Remove(tag);
        }
    }

    public async Task RemoveAllAsync(
        Guid fileId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Materialize then RemoveRange — RemoveRange expects tracked entities. ExecuteDeleteAsync
        // would be cheaper, but we want the EF tracker to be consistent with the service's
        // `SaveChangesAsync` unit of work so callers can compose this call with other state
        // changes in the same transaction.
        var tags = await db.Tags
            .Where(t => t.FileId == fileId && t.UserId == userId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        db.Tags.RemoveRange(tags);
    }

    public async Task<IReadOnlyList<Tag>> SearchAsync(
        Guid tenantId,
        Guid userId,
        string? key,
        string? value,
        CancellationToken cancellationToken = default)
    {
        // tenantId on the signature is informational — the global query filter already pins
        // `t.TenantId == currentTenant`. Accepting the parameter forces callers to state intent
        // explicitly and leaves room to extend to admin-cross-tenant search later without
        // breaking the method signature.
        var query = db.Tags.Where(t => t.UserId == userId);
        if (!string.IsNullOrEmpty(key))
        {
            var normalizedKey = key.ToLowerInvariant();
            query = query.Where(t => t.Key == normalizedKey);
        }
        if (!string.IsNullOrEmpty(value))
        {
            query = query.Where(t => t.Value == value);
        }
        return await query
            .OrderBy(t => t.Key)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
