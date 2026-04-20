using GreenDonut;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.DataLoaders;

public sealed class FileItemByIdDataLoader : BatchDataLoader<Guid, FileItem>
{
    private readonly IDbContextFactory<StrgDbContext> _dbFactory;

    public FileItemByIdDataLoader(
        IDbContextFactory<StrgDbContext> dbFactory,
        IBatchScheduler batchScheduler,
        DataLoaderOptions? options = null)
        : base(batchScheduler, options ?? new DataLoaderOptions())
    {
        _dbFactory = dbFactory;
    }

    protected override async Task<IReadOnlyDictionary<Guid, FileItem>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Files
            .Where(f => keys.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, ct);
    }
}
