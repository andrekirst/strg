using Mediator;
using Microsoft.EntityFrameworkCore;
using Strg.Application.Abstractions;
using Strg.Core;
using Strg.Core.Domain;

namespace Strg.Application.Features.Users.UpdateQuota;

/// <summary>
/// Looks the user up through the tenant-filtered <c>StrgDbContext.Users</c> set, mutates
/// <see cref="User.QuotaBytes"/>, saves. Cross-tenant ids collapse to NotFound through the
/// global filter, which the endpoint maps to HTTP 404 — the same anti-enumeration stance every
/// other admin lookup follows.
/// </summary>
internal sealed class UpdateUserQuotaHandler(IStrgDbContext db)
    : ICommandHandler<UpdateUserQuotaCommand, Result<User>>
{
    public async ValueTask<Result<User>> Handle(UpdateUserQuotaCommand command, CancellationToken cancellationToken)
    {
        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            return Result<User>.Failure("NotFound", "User not found.");
        }

        user.QuotaBytes = command.QuotaBytes;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<User>.Success(user);
    }
}
