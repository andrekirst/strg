using HotChocolate.Authorization;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Services;
using Strg.GraphQL.Inputs.User;
using Strg.GraphQL.Payloads;
using Strg.GraphQL.Payloads.User;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.Mutations.User;

[ExtendObjectType<UserMutations>]
public sealed class UserMutationHandlers
{
    [Authorize]
    public async Task<UpdateProfilePayload> UpdateProfileAsync(
        UpdateProfileInput input,
        [Service] StrgDbContext db,
        [GlobalState("userId")] Guid userId,
        CancellationToken cancellationToken)
    {
        if (input.DisplayName?.Length > 255)
            return new UpdateProfilePayload(null,
                [new UserError("VALIDATION_ERROR", "displayName must be ≤255 chars.", "displayName")]);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return new UpdateProfilePayload(null, [new UserError("NOT_FOUND", "User not found.", null)]);

        if (input.DisplayName is not null) user.DisplayName = input.DisplayName;
        if (input.Email is not null) user.Email = input.Email;
        await db.SaveChangesAsync(cancellationToken);
        return new UpdateProfilePayload(user, null);
    }

    [Authorize]
    public async Task<ChangePasswordPayload> ChangePasswordAsync(
        ChangePasswordInput input,
        [Service] IPasswordHasher passwordHasher,
        [Service] StrgDbContext db,
        [GlobalState("userId")] Guid userId,
        CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return new ChangePasswordPayload(null, [new UserError("NOT_FOUND", "User not found.", null)]);

        if (!passwordHasher.Verify(input.CurrentPassword, user.PasswordHash))
            return new ChangePasswordPayload(null,
                [new UserError("INVALID_PASSWORD", "Current password is incorrect.", "currentPassword")]);

        user.PasswordHash = passwordHasher.Hash(input.NewPassword);
        await db.SaveChangesAsync(cancellationToken);
        return new ChangePasswordPayload(user, null);
    }
}
