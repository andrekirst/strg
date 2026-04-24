using Mediator;
using Microsoft.EntityFrameworkCore;
using Strg.Application.Abstractions;
using Strg.Core.Domain;

namespace Strg.Application.Features.Drives.Get;

internal sealed class GetDriveHandler(IStrgDbContext db) : IQueryHandler<GetDriveQuery, Drive?>
{
    public async ValueTask<Drive?> Handle(GetDriveQuery query, CancellationToken cancellationToken)
        => await db.Drives.FirstOrDefaultAsync(d => d.Id == query.Id, cancellationToken).ConfigureAwait(false);
}
