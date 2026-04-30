using Mediator;
using Microsoft.EntityFrameworkCore;
using Strg.Application.Abstractions;
using Strg.Core.Domain;

namespace Strg.Application.Features.Files.List;

/// <summary>
/// Handles <see cref="ListFilesQuery"/>. The drive existence + tenant scoping check is run via
/// <c>db.Drives.AnyAsync</c> and relies on <c>StrgDbContext</c>'s global tenant filter — a
/// foreign-tenant drive id returns false here and the handler emits null, which the endpoint
/// surfaces as HTTP 404 (matching the established <c>GetDriveQuery</c> contract).
///
/// <para><b>Path-based filtering, not ParentId-based.</b> Two of the three FileItem-creation
/// sites (<c>StrgTusStore</c>, <c>CreateFolderHandler</c>) leave <c>ParentId</c> null, so a
/// ParentId filter would silently miss most production rows. <see cref="FileItem.Path"/> is the
/// authoritative locator, populated through <c>StoragePath.Normalize</c> on the upload path —
/// no leading slash, no trailing slash. The prefix filter below uses that exact format.</para>
///
/// <para>The 200-item pageSize cap is also enforced at the endpoint; replicating it here is
/// defence-in-depth so a programmatic Mediator caller (a future internal feature, a GraphQL
/// adapter, etc.) can't blow the ceiling either.</para>
/// </summary>
internal sealed class ListFilesHandler(IStrgDbContext db, ICurrentUser currentUser)
    : IQueryHandler<ListFilesQuery, ListFilesResult?>
{
    private const int MaxPageSize = 200;

    public async ValueTask<ListFilesResult?> Handle(ListFilesQuery query, CancellationToken cancellationToken)
    {
        var driveExists = await db.Drives
            .AnyAsync(d => d.Id == query.DriveId, cancellationToken)
            .ConfigureAwait(false);
        if (!driveExists)
        {
            return null;
        }

        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var page = Math.Max(query.Page, 1);

        var prefix = NormalizePrefix(query.Path);
        var filtered = ApplyPathFilter(db.Files.Where(f => f.DriveId == query.DriveId), prefix, query.Recursive);

        if (query.TagKey is not null)
        {
            // Lowercase the input to match Tag.Key's init-time normalization (Strg.Core.Domain.Tag).
            // The inline `t.UserId == userId` predicate is defence-in-depth on top of the Tag
            // user-scope global query filter — survives a hypothetical filter-bypass carve-out
            // without leaking another user's tagged files. Tag.Value matches case-sensitively.
            var tagKey = query.TagKey.ToLowerInvariant();
            var tagValue = query.TagValue;
            var userId = currentUser.UserId;
            filtered = filtered.Where(f => f.Tags.Any(t =>
                t.UserId == userId
                && t.Key == tagKey
                && (tagValue == null || t.Value == tagValue)));
        }

        var totalCount = await filtered.CountAsync(cancellationToken).ConfigureAwait(false);

        var items = await filtered
            .OrderBy(f => f.IsDirectory ? 0 : 1)  // Directories first — matches the directory-tree UX.
            .ThenBy(f => f.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ListFilesResult(items, page, pageSize, totalCount);
    }

    /// <summary>
    /// Aligns the caller-supplied path with the storage convention: empty string for root, no
    /// leading or trailing slash otherwise. Production paths exit <c>StoragePath.Normalize</c>
    /// in the same shape, so the resulting prefix concatenated with "/" matches an indexed
    /// substring of <see cref="FileItem.Path"/>.
    /// </summary>
    private static string NormalizePrefix(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }
        return raw.Trim().Trim('/');
    }

    private static IQueryable<FileItem> ApplyPathFilter(IQueryable<FileItem> source, string prefix, bool recursive)
    {
        if (prefix.Length == 0)
        {
            // Root listing. Recursive returns every row in the drive; non-recursive returns only
            // top-level entries (Path = "<name>" with no internal slash).
            return recursive
                ? source
                : source.Where(f => !f.Path.Contains("/"));
        }

        // Sub-folder listing. Files under prefix "docs" have Path = "docs/<rest>". The folder
        // entry itself (Path == "docs") is intentionally excluded — listing the contents of /docs
        // should not include /docs.
        var prefixSlash = prefix + "/";
        return recursive
            ? source.Where(f => f.Path.StartsWith(prefixSlash))
            : source.Where(f => f.Path.StartsWith(prefixSlash)
                             && !f.Path.Substring(prefixSlash.Length).Contains("/"));
    }
}
