using HotChocolate.Authorization;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.GraphQl.Queries.Admin;
using Strg.GraphQl.Queries.Storage;
using Strg.Infrastructure.Data;

namespace Strg.GraphQl.Queries;

[ExtendObjectType("Query")]
public sealed class RootQueryExtension
{
    [Authorize]
    public async Task<User> Me(
        [Service] StrgDbContext db,
        [GlobalState("userId")] Guid userId,
        CancellationToken cancellationToken)
        => await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
           ?? throw new UnauthorizedAccessException();

    public StorageQueries Storage() => new();

    public InboxQueries Inbox() => new();

    [Authorize(Policy = "Admin")]
    public AdminQueries Admin() => new();
}

public sealed record InboxQueries
{
    public bool Placeholder => false;
}
