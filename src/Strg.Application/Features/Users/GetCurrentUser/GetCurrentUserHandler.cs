using Mediator;
using Microsoft.EntityFrameworkCore;
using Strg.Application.Abstractions;
using Strg.Core.Domain;

namespace Strg.Application.Features.Users.GetCurrentUser;

/// <summary>
/// Resolves the user id from <see cref="ICurrentUser"/> (sub claim) rather than accepting it
/// on the wire. The lookup runs against the global-filtered <c>StrgDbContext.Users</c> set —
/// a soft-deleted or cross-tenant row collapses to <see langword="null"/> automatically, so
/// the endpoint surfaces 404 without a separate predicate. The global filter is the security
/// boundary the rest of the application relies on; bypassing it would defeat tenant isolation.
/// </summary>
internal sealed class GetCurrentUserHandler(IStrgDbContext db, ICurrentUser currentUser)
    : IQueryHandler<GetCurrentUserQuery, User?>
{
    public async ValueTask<User?> Handle(GetCurrentUserQuery query, CancellationToken cancellationToken)
        => await db.Users
            .FirstOrDefaultAsync(u => u.Id == currentUser.UserId, cancellationToken)
            .ConfigureAwait(false);
}
