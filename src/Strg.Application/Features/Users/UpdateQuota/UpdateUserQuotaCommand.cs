using Mediator;
using Strg.Core;
using Strg.Core.Domain;

namespace Strg.Application.Features.Users.UpdateQuota;

/// <summary>
/// STRG-059 — admin-issued update of a user's <see cref="User.QuotaBytes"/>. Authorization
/// (admin scope) is enforced at the REST endpoint via <c>AuthPolicies.Admin</c>; the command
/// itself is unaware of role checks so the Application layer carries no policy knowledge.
///
/// <para>Returns <see cref="Result{T}"/> over <see cref="User"/> so validation failures
/// (<c>QuotaBytes &lt; 0</c>) surface via <c>ErrorCode = "ValidationError"</c> from the
/// validation pipeline behavior — the REST shim maps them to HTTP 400.</para>
/// </summary>
public sealed record UpdateUserQuotaCommand(Guid UserId, long QuotaBytes) : ICommand<Result<User>>;
