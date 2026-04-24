using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.GraphQl.DataLoaders;

public sealed class DriveByIdDataLoader(
    IDbContextFactory<StrgDbContext> dbFactory,
    IBatchScheduler batchScheduler,
    DataLoaderOptions? options = null)
    : BatchDataLoader<Guid, Drive>(batchScheduler, options ?? new DataLoaderOptions())
{
    protected override async Task<IReadOnlyDictionary<Guid, Drive>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Drives
            .Where(d => keys.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, cancellationToken);
    }
}
