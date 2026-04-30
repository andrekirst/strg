using Mediator;
using Microsoft.EntityFrameworkCore;
using Strg.Application.Abstractions;
using Strg.Core.Domain;

namespace Strg.Application.Features.Users.Unlock;

internal sealed class UnlockUserHandler(IStrgDbContext db)
    : ICommandHandler<UnlockUserCommand, User?>
{
    public async ValueTask<User?> Handle(UnlockUserCommand command, CancellationToken cancellationToken)
    {
        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            return null;
        }

        user.LockedUntil = null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return user;
    }
}
