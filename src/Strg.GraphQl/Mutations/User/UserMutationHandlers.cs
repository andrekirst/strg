using HotChocolate.Authorization;
using Microsoft.EntityFrameworkCore;
using Strg.Core;
using Strg.Core.Identity;
using Strg.GraphQl.Inputs.User;
using Strg.GraphQl.Payloads;
using Strg.GraphQl.Payloads.User;
using Strg.Infrastructure.Data;

namespace Strg.GraphQl.Mutations.User;

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
        {
            return new UpdateProfilePayload(null,
                [new UserError("VALIDATION_ERROR", "displayName must be ≤255 chars.", "displayName")]);
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return new UpdateProfilePayload(null, [new UserError("NOT_FOUND", "User not found.", null)]);
        }

        if (input.DisplayName is not null)
        {
            user.DisplayName = input.DisplayName;
        }

        if (input.Email is not null)
        {
            user.Email = input.Email;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new UpdateProfilePayload(user, null);
    }

    [Authorize]
    public async Task<ChangePasswordPayload> ChangePasswordAsync(
        ChangePasswordInput input,
        [Service] IUserManager userManager,
        [Service] StrgDbContext db,
        [GlobalState("userId")] Guid userId,
        CancellationToken cancellationToken)
    {
        // Delegate to IUserManager.ChangePasswordAsync for THREE invariants that MUST NOT be
        // re-implemented at the resolver layer:
        //   (1) MinimumPasswordLength gate (UserManagerErrors.MinimumPasswordLength = 12). A prior
        //       incarnation of this handler re-implemented verify+hash inline and silently dropped
        //       this check, letting an authenticated user set a 1-char password through GraphQL
        //       while REST refused the same input (STRG-073 MED-5 — the password-policy bypass
        //       that drove this refactor).
        //   (2) PBKDF2 verify of the current password against the stored hash.
        //   (3) UserPasswordChangedEvent publish through MassTransit's EF outbox, which the
        //       WebDavJwtCacheInvalidationConsumer uses to evict the cached Basic-Auth → JWT
        //       exchange. Duplicating the publish here would double-evict (idempotent but
        //       wasteful); omitting it on a hand-rolled path would reopen the 14-min stale-
        //       credential window the consumer exists to close.
        // Delegation collapses the three duplicated invariants to a single enforcement site and
        // makes future drift impossible — the next refactor that changes any of the three only
        // has one place to change.
        var result = await userManager.ChangePasswordAsync(
            userId, input.CurrentPassword, input.NewPassword, cancellationToken);

        if (result.IsFailure)
        {
            return new ChangePasswordPayload(null, [MapChangePasswordError(result)]);
        }

        // Reload under the same scoped DbContext — UserManager has already committed via its
        // SaveChangesAsync, so this returns the post-change row. Required because
        // IUserManager.ChangePasswordAsync returns unit Result, while ChangePasswordPayload's
        // schema carries the User for post-mutation client refresh (DisplayName, Role, etc.
        // unchanged but the payload contract is non-null on success).
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return new ChangePasswordPayload(user, null);
    }

    // Maps stable IUserManager error codes to the wire-level UserError shape. The UserError.Code
    // strings are part of the GraphQL contract (clients branch on them); keeping this mapping
    // explicit prevents a UserManager error-code rename from silently breaking clients, and
    // makes the field association (newPassword vs currentPassword) intentional rather than
    // inferred.
    private static UserError MapChangePasswordError(Result result) => result.ErrorCode switch
    {
        UserManagerErrors.UserNotFound =>
            new UserError("NOT_FOUND", result.ErrorMessage ?? "User not found.", null),
        UserManagerErrors.InvalidPassword =>
            new UserError("INVALID_PASSWORD", result.ErrorMessage ?? "Current password is incorrect.", "currentPassword"),
        UserManagerErrors.PasswordTooShort =>
            new UserError("VALIDATION_ERROR",
                result.ErrorMessage ?? $"Password must be at least {UserManagerErrors.MinimumPasswordLength} characters.",
                "newPassword"),
        _ => new UserError("VALIDATION_ERROR", result.ErrorMessage ?? "Validation error.", null),
    };
}
