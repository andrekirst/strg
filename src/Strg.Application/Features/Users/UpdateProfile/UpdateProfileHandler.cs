using Mediator;
using Microsoft.EntityFrameworkCore;
using Strg.Application.Abstractions;
using Strg.Core;
using Strg.Core.Domain;

namespace Strg.Application.Features.Users.UpdateProfile;

/// <summary>
/// Loads the current user via <see cref="ICurrentUser"/> and mutates <see cref="User.DisplayName"/>.
/// The tenant filter on <c>StrgDbContext.Users</c> scopes the lookup automatically — a token whose
/// <c>sub</c> claim points at a user from another tenant collapses to <see langword="null"/>,
/// surfacing as HTTP 404 (an honest "we don't see that user" response that does not double as a
/// cross-tenant existence oracle).
/// </summary>
internal sealed class UpdateProfileHandler(IStrgDbContext db, ICurrentUser currentUser)
    : ICommandHandler<UpdateProfileCommand, Result<User>>
{
    public async ValueTask<Result<User>> Handle(UpdateProfileCommand command, CancellationToken cancellationToken)
    {
        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Id == currentUser.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            return Result<User>.Failure("NotFound", "Current user not found.");
        }

        user.DisplayName = command.DisplayName;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<User>.Success(user);
    }
}
