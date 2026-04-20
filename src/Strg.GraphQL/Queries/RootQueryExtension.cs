using HotChocolate.Authorization;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.GraphQL.Queries.Admin;
using Strg.GraphQL.Queries.Storage;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.Queries;

[ExtendObjectType("Query")]
public sealed class RootQueryExtension
{
    [Authorize]
    public async Task<User> Me(
        [Service] StrgDbContext db,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
        => await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
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
