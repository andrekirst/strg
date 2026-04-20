using HotChocolate.Authorization;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;
using Strg.GraphQL.Inputs.Admin;
using Strg.GraphQL.Payloads;
using Strg.GraphQL.Payloads.User;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.Mutations.Admin;

[ExtendObjectType<AdminMutations>]
public sealed class AdminMutationHandlers
{
    [Authorize(Policy = "AdminWrite")]
    public async Task<UpdateUserQuotaPayload> UpdateUserQuotaAsync(
        UpdateUserQuotaInput input,
        [Service] StrgDbContext db,
        CancellationToken cancellationToken)
    {
        if (input.QuotaBytes < 0)
            return new UpdateUserQuotaPayload(null,
                [new UserError("VALIDATION_ERROR", "quotaBytes must be non-negative.", "quotaBytes")]);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == input.UserId, cancellationToken);
        if (user is null)
            return new UpdateUserQuotaPayload(null, [new UserError("NOT_FOUND", "User not found.", null)]);

        user.QuotaBytes = input.QuotaBytes;
        await db.SaveChangesAsync(cancellationToken);
        return new UpdateUserQuotaPayload(user, null);
    }

    [Authorize(Policy = "AdminWrite")]
    public async Task<LockUserPayload> LockUserAsync(
        LockUserInput input,
        [Service] StrgDbContext db,
        CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == input.UserId, cancellationToken);
        if (user is null)
            return new LockUserPayload(null, [new UserError("NOT_FOUND", "User not found.", null)]);

        user.LockedUntil = DateTimeOffset.UtcNow.AddYears(100);
        await db.SaveChangesAsync(cancellationToken);
        return new LockUserPayload(user, null);
    }

    [Authorize(Policy = "AdminWrite")]
    public async Task<UnlockUserPayload> UnlockUserAsync(
        UnlockUserInput input,
        [Service] StrgDbContext db,
        CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == input.UserId, cancellationToken);
        if (user is null)
            return new UnlockUserPayload(null, [new UserError("NOT_FOUND", "User not found.", null)]);

        user.LockedUntil = null;
        await db.SaveChangesAsync(cancellationToken);
        return new UnlockUserPayload(user, null);
    }
}
