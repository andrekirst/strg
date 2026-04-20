using HotChocolate.Authorization;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.GraphQL.Inputs.File;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.Queries.Storage;

[ExtendObjectType<StorageQueries>]
public sealed class FileQueries
{
    [UsePaging(IncludeTotalCount = true, DefaultPageSize = 50, MaxPageSize = 200)]
    [Authorize(Policy = "FilesRead")]
    public IQueryable<FileItem> GetFiles(
        Guid driveId,
        string? path,
        FileFilterInput? filter,
        [Service] StrgDbContext db)
    {
        var query = db.Files.Where(f => f.DriveId == driveId);

        if (path is not null)
            query = query.Where(f => f.Path.StartsWith(path));
        if (filter?.NameContains is not null)
            query = query.Where(f => f.Name.Contains(filter.NameContains));
        if (filter?.IsFolder.HasValue == true)
            query = query.Where(f => f.IsDirectory == filter.IsFolder.Value);
        if (filter?.MinSize.HasValue == true)
            query = query.Where(f => f.Size >= filter.MinSize.Value);
        if (filter?.MaxSize.HasValue == true)
            query = query.Where(f => f.Size <= filter.MaxSize.Value);
        if (filter?.CreatedAfter.HasValue == true)
            query = query.Where(f => f.CreatedAt >= filter.CreatedAfter.Value);
        if (filter?.CreatedBefore.HasValue == true)
            query = query.Where(f => f.CreatedAt <= filter.CreatedBefore.Value);
        if (filter?.IsInInbox.HasValue == true)
            query = query.Where(f => f.IsInInbox == filter.IsInInbox.Value);
        if (filter?.MimeType is not null)
        {
            var mime = filter.MimeType;
            if (mime.EndsWith("/*"))
            {
                var prefix = mime.Substring(0, mime.Length - 2);
                query = query.Where(f => f.MimeType != null && f.MimeType.StartsWith(prefix));
            }
            else
                query = query.Where(f => f.MimeType == mime);
        }

        return query;
    }

    [Authorize(Policy = "FilesRead")]
    public Task<FileItem?> GetFile(
        Guid id,
        [Service] StrgDbContext db,
        CancellationToken cancellationToken)
        => db.Files.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
}
