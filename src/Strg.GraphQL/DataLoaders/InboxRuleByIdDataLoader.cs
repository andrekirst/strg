using GreenDonut;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.DataLoaders;

public sealed class InboxRuleByIdDataLoader : BatchDataLoader<Guid, InboxRule>
{
    private readonly IDbContextFactory<StrgDbContext> _dbFactory;

    public InboxRuleByIdDataLoader(
        IDbContextFactory<StrgDbContext> dbFactory,
        IBatchScheduler batchScheduler,
        DataLoaderOptions? options = null)
        : base(batchScheduler, options ?? new DataLoaderOptions())
    {
        _dbFactory = dbFactory;
    }

    protected override async Task<IReadOnlyDictionary<Guid, InboxRule>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.InboxRules
            .Where(r => keys.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, cancellationToken);
    }
}
