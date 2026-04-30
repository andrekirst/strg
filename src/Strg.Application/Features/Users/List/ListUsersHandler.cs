using Mediator;
using Microsoft.EntityFrameworkCore;
using Strg.Application.Abstractions;

namespace Strg.Application.Features.Users.List;

/// <summary>
/// Returns a deterministic page over the tenant's user set. Ordering is
/// <c>CreatedAt</c> ascending then <c>Email</c> ascending — stable for paginated reads, and
/// matches the natural creation-order admins expect when scanning a tenant. The 200-item
/// <see cref="MaxPageSize"/> cap is also enforced at the endpoint; replicating it here is
/// defence-in-depth for non-HTTP callers.
///
/// <para>Tenant scoping is the global query filter on <c>StrgDbContext.Users</c>. No explicit
/// <c>Where(u =&gt; u.TenantId == tenantContext.TenantId)</c> is added — the redundant
/// predicate would mislead readers into thinking the filter wasn't already enforced.</para>
/// </summary>
internal sealed class ListUsersHandler(IStrgDbContext db)
    : IQueryHandler<ListUsersQuery, ListUsersResult>
{
    private const int MaxPageSize = 200;

    public async ValueTask<ListUsersResult> Handle(ListUsersQuery query, CancellationToken cancellationToken)
    {
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var page = Math.Max(query.Page, 1);

        var totalCount = await db.Users.CountAsync(cancellationToken).ConfigureAwait(false);

        var items = await db.Users
            .OrderBy(u => u.CreatedAt)
            .ThenBy(u => u.Email)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ListUsersResult(items, page, pageSize, totalCount);
    }
}
