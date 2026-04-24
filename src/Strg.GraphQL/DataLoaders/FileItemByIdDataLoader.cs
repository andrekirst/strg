using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.DataLoaders;

public sealed class FileItemByIdDataLoader(
    IDbContextFactory<StrgDbContext> dbFactory,
    IBatchScheduler batchScheduler,
    DataLoaderOptions? options = null)
    : BatchDataLoader<Guid, FileItem>(batchScheduler, options ?? new DataLoaderOptions())
{
    protected override async Task<IReadOnlyDictionary<Guid, FileItem>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Files
            .Where(f => keys.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, cancellationToken);
    }
}
