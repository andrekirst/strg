using GreenDonut;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.DataLoaders;

public sealed class DriveByIdDataLoader : BatchDataLoader<Guid, Drive>
{
    private readonly IDbContextFactory<StrgDbContext> _dbFactory;

    public DriveByIdDataLoader(
        IDbContextFactory<StrgDbContext> dbFactory,
        IBatchScheduler batchScheduler,
        DataLoaderOptions? options = null)
        : base(batchScheduler, options ?? new DataLoaderOptions())
    {
        _dbFactory = dbFactory;
    }

    protected override async Task<IReadOnlyDictionary<Guid, Drive>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Drives
            .Where(d => keys.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, cancellationToken);
    }
}
