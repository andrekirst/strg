using Mediator;
using Strg.Core.Domain;

namespace Strg.Application.Features.Users.Unlock;

/// <summary>
/// STRG-059 — admin-issued account unlock. Clears <see cref="User.LockedUntil"/> to
/// <see langword="null"/>, after which <see cref="User.IsLocked"/> returns
/// <see langword="false"/>. Authorization (admin scope) is enforced at the REST endpoint via
/// <c>AuthPolicies.Admin</c>; the handler stays unaware of policy. A <see langword="null"/>
/// return surfaces as HTTP 404 — the user does not exist in the current tenant.
/// </summary>
public sealed record UnlockUserCommand(Guid UserId) : ICommand<User?>;
