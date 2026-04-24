using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.GraphQl.DataLoaders;

public sealed class InboxRuleByIdDataLoader(
    IDbContextFactory<StrgDbContext> dbFactory,
    IBatchScheduler batchScheduler,
    DataLoaderOptions? options = null)
    : BatchDataLoader<Guid, InboxRule>(batchScheduler, options ?? new DataLoaderOptions())
{
    protected override async Task<IReadOnlyDictionary<Guid, InboxRule>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.InboxRules
            .Where(r => keys.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, cancellationToken);
    }
}
