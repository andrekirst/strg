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
        //
        // Tenant-ownership of `tag.FileId` is NOT enforced here: `Tag` has no EF navigation/FK
        // onto `FileItem`, so a foreign-tenant fileId would INSERT a ghost row. The guard lives
        // at the service layer (`TagService` verifies the file is visible via the tenant-filtered
        // `IFileRepository.GetByIdAsync` before calling this method). Any future caller of this
        // repository MUST replicate that guard — the repo trusts its caller on tenant ownership.
        // Tracked as v0.2 structural fix: composite `(FileId, TenantId) → FileItem(Id, TenantId)`
        // FK would move the defense down into the DB and retire the service-layer obligation.
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
        Guid userId,
        string? key,
        string? value,
        CancellationToken cancellationToken = default)
    {
        // Tenant scoping is ambient via the global query filter (`t.TenantId == currentTenant`).
        // The previous signature accepted a tenantId parameter "informationally" — dropped because
        // it was a footgun: a reviewer seeing `SearchAsync(adminTargetTenantId, ...)` would assume
        // cross-tenant lookup was possible here. When admin-cross-tenant search legitimately lands,
        // introduce `ITagAdminService` (same pattern as `IQuotaAdminService`) rather than
        // overloading this user-facing repo.
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
