using Mediator;
using Microsoft.EntityFrameworkCore;
using Strg.Application.Abstractions;
using Strg.Core.Domain;

namespace Strg.Application.Features.Users.GetUserById;

internal sealed class GetUserByIdHandler(IStrgDbContext db)
    : IQueryHandler<GetUserByIdQuery, User?>
{
    public async ValueTask<User?> Handle(GetUserByIdQuery query, CancellationToken cancellationToken)
        => await db.Users
            .FirstOrDefaultAsync(u => u.Id == query.UserId, cancellationToken)
            .ConfigureAwait(false);
}
