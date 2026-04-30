using Mediator;
using Strg.Core.Domain;

namespace Strg.Application.Features.Users.Lock;

/// <summary>
/// STRG-059 — admin-issued account lock. Sets <see cref="User.LockedUntil"/> to a far-future
/// timestamp (<c>UtcNow + 100 years</c>) so the existing <see cref="User.IsLocked"/> computed
/// property returns <see langword="true"/> from now until the lock is explicitly released via
/// <see cref="Strg.Application.Features.Users.Unlock.UnlockUserCommand"/>. The choice of
/// "+100 years" (rather than <see cref="DateTimeOffset.MaxValue"/>) mirrors the existing
/// GraphQL admin handler (<c>AdminMutationHandlers.cs:48</c>) and avoids overflow edge cases
/// arising from arithmetic on <see cref="DateTimeOffset.MaxValue"/>.
///
/// <para>No validator → returns plain <c>User?</c>; the endpoint maps <see langword="null"/> to
/// HTTP 404. Authorization (admin scope) is enforced at the REST endpoint via
/// <c>AuthPolicies.Admin</c>.</para>
/// </summary>
public sealed record LockUserCommand(Guid UserId) : ICommand<User?>;
