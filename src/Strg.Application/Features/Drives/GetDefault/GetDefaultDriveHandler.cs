using Mediator;
using Microsoft.EntityFrameworkCore;
using Strg.Application.Abstractions;
using Strg.Core.Domain;

namespace Strg.Application.Features.Drives.GetDefault;

internal sealed class GetDefaultDriveHandler(
    IStrgDbContext db,
    ICurrentUser currentUser)
    : IQueryHandler<GetDefaultDriveQuery, Drive?>
{
    public async ValueTask<Drive?> Handle(GetDefaultDriveQuery query, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;

        // Step 1: per-user override (UserDriveDefault tenant filter scopes to current tenant).
        var preference = await db.UserDriveDefaults
            .FirstOrDefaultAsync(d => d.UserId == userId, cancellationToken)
            .ConfigureAwait(false);
        if (preference is not null)
        {
            // Soft-deleted drive ⇒ filter hides it ⇒ falls through to the tenant default. Stale
            // UserDriveDefault rows are intentionally not garbage-collected here; cleanup is
            // tracked as a follow-up if it becomes a real problem.
            var picked = await db.Drives
                .FirstOrDefaultAsync(d => d.Id == preference.DriveId, cancellationToken)
                .ConfigureAwait(false);
            if (picked is not null)
            {
                return picked;
            }
        }

        // Step 2: tenant-wide bootstrap default.
        return await db.Drives
            .FirstOrDefaultAsync(d => d.IsDefault, cancellationToken)
            .ConfigureAwait(false);
    }
}
