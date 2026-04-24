using Mediator;
using Strg.Core.Domain;

namespace Strg.Application.Features.Drives.List;

/// <summary>
/// Lists every tenant-scoped drive, ordered by name. Scoped automatically via StrgDbContext's
/// global tenant filter. Used by the REST surface; the GraphQL <c>GetDrives</c> query stays on
/// <see cref="IQueryable{T}"/> so HotChocolate's <c>[UsePaging]</c> middleware can push the page
/// boundary into SQL — that middleware does not compose over <c>IReadOnlyList</c>.
/// </summary>
public sealed record ListDrivesQuery : IQuery<IReadOnlyList<Drive>>;
