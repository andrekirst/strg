using Mediator;
using Microsoft.EntityFrameworkCore;
using Strg.Application.Abstractions;
using Strg.Core.Domain;

namespace Strg.Application.Features.Users.Lock;

internal sealed class LockUserHandler(IStrgDbContext db)
    : ICommandHandler<LockUserCommand, User?>
{
    public async ValueTask<User?> Handle(LockUserCommand command, CancellationToken cancellationToken)
    {
        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            return null;
        }

        // +100 years matches the existing GraphQL admin mutation (AdminMutationHandlers.cs:48)
        // — chosen over DateTimeOffset.MaxValue because it dodges overflow edge cases that
        // arise when downstream code does additive math on the timestamp (e.g. a hypothetical
        // "extend lock by 1 day" feature).
        user.LockedUntil = DateTimeOffset.UtcNow.AddYears(100);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return user;
    }
}
