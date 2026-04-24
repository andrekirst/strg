using Mediator;
using Microsoft.EntityFrameworkCore;
using Strg.Application.Abstractions;
using Strg.Core.Domain;

namespace Strg.Application.Features.Drives.List;

internal sealed class ListDrivesHandler(IStrgDbContext db) : IQueryHandler<ListDrivesQuery, IReadOnlyList<Drive>>
{
    public async ValueTask<IReadOnlyList<Drive>> Handle(ListDrivesQuery query, CancellationToken cancellationToken)
        => await db.Drives.OrderBy(d => d.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
}
