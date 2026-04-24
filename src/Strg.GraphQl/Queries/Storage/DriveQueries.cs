using HotChocolate.Authorization;
using Mediator;
using Strg.Application.Features.Drives.Get;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.GraphQl.Queries.Storage;

[ExtendObjectType<StorageQueries>]
public sealed class DriveQueries
{
    // GetDrives intentionally stays as a synchronous IQueryable-returning resolver so
    // HotChocolate's [UsePaging] middleware can compose a LIMIT/OFFSET SQL translation over the
    // underlying query. Wrapping this call in IMediator would force the handler to materialise
    // the list — the middleware then paginates in-memory, defeating the purpose. The tenant
    // filter on StrgDbContext still applies implicitly (no IgnoreQueryFilters here), and the
    // FilesRead policy gate is the authorization boundary. The concrete StrgDbContext is injected
    // rather than IStrgDbContext so existing test fixtures that only register the concrete type
    // keep working — migrating this read path through the Application layer is a Phase 3+ concern
    // tracked in the plan.
    [UsePaging(IncludeTotalCount = true, DefaultPageSize = 50, MaxPageSize = 200)]
    [Authorize(Policy = "FilesRead")]
    public IQueryable<Drive> GetDrives([Service] StrgDbContext db)
        => db.Drives.OrderBy(d => d.Name);

    [Authorize(Policy = "FilesRead")]
    public async Task<Drive?> GetDrive(
        Guid id,
        [Service] IMediator mediator,
        CancellationToken cancellationToken)
        => await mediator.Send(new GetDriveQuery(id), cancellationToken);
}
