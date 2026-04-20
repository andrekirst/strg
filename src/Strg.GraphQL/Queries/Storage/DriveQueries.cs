using HotChocolate.Authorization;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.Queries.Storage;

[ExtendObjectType<StorageQueries>]
public sealed class DriveQueries
{
    [UsePaging(IncludeTotalCount = true, DefaultPageSize = 50, MaxPageSize = 200)]
    [Authorize(Policy = "FilesRead")]
    public IQueryable<Drive> GetDrives([Service] StrgDbContext db)
        => db.Drives.OrderBy(d => d.Name);

    [Authorize(Policy = "FilesRead")]
    public Task<Drive?> GetDrive(
        Guid id, [Service] StrgDbContext db, CancellationToken cancellationToken)
        => db.Drives.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
}
