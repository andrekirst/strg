using HotChocolate.Authorization;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.GraphQl.Inputs.Admin;
using Strg.Infrastructure.Data;

namespace Strg.GraphQl.Queries.Admin;

[ExtendObjectType<AdminQueries>]
public sealed class AuditLogQueries
{
    [UsePaging(IncludeTotalCount = true, DefaultPageSize = 50, MaxPageSize = 500)]
    [Authorize(Policy = "AdminRead")]
    public IQueryable<AuditEntry> GetAuditLog(
        AuditFilterInput? filter,
        [Service] StrgDbContext db)
    {
        IQueryable<AuditEntry> query = db.AuditEntries.OrderByDescending(e => e.PerformedAt);

        if (filter?.UserId.HasValue == true)
        {
            query = query.Where(e => e.UserId == filter.UserId.Value);
        }

        if (filter?.Action is not null)
        {
            query = query.Where(e => e.Action == filter.Action);
        }

        if (filter?.ResourceType is not null)
        {
            query = query.Where(e => e.ResourceType == filter.ResourceType);
        }

        if (filter?.From.HasValue == true)
        {
            query = query.Where(e => e.PerformedAt >= filter.From.Value);
        }

        if (filter?.To.HasValue == true)
        {
            query = query.Where(e => e.PerformedAt <= filter.To.Value);
        }

        return query;
    }

    [UsePaging(IncludeTotalCount = true, DefaultPageSize = 50, MaxPageSize = 200)]
    [Authorize(Policy = "AdminRead")]
    public IQueryable<User> GetUsers([Service] StrgDbContext db)
        => db.Users.OrderBy(u => u.Email);

    [Authorize(Policy = "AdminRead")]
    public Task<User?> GetUser(
        Guid id,
        [Service] StrgDbContext db,
        CancellationToken cancellationToken)
        => db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
}
