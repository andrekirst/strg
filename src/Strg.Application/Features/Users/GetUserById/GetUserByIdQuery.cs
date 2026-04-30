using Mediator;
using Strg.Core.Domain;

namespace Strg.Application.Features.Users.GetUserById;

/// <summary>
/// STRG-059 — returns a user from the current tenant by id, or <see langword="null"/> when the
/// id does not match any tenant-visible row. Authorization (admin scope) is enforced at the
/// endpoint via <c>AuthPolicies.Admin</c>; this query stays unaware of role checks so the
/// Application layer carries no policy knowledge. The global tenant filter on
/// <c>StrgDbContext.Users</c> guarantees cross-tenant ids collapse to <see langword="null"/>
/// → HTTP 404, preventing a cross-tenant id-enumeration oracle.
/// </summary>
public sealed record GetUserByIdQuery(Guid UserId) : IQuery<User?>;
