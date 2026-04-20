using GreenDonut;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.DataLoaders;

public sealed class UserByIdDataLoader : BatchDataLoader<Guid, User>
{
    private readonly IDbContextFactory<StrgDbContext> _dbFactory;

    public UserByIdDataLoader(
        IDbContextFactory<StrgDbContext> dbFactory,
        IBatchScheduler batchScheduler,
        DataLoaderOptions? options = null)
        : base(batchScheduler, options ?? new DataLoaderOptions())
    {
        _dbFactory = dbFactory;
    }

    protected override async Task<IReadOnlyDictionary<Guid, User>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Users
            .Where(u => keys.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);
    }
}
