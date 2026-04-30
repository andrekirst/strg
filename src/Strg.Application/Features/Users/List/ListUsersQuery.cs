using Mediator;

namespace Strg.Application.Features.Users.List;

/// <summary>
/// STRG-059 — paginated list of every user in the current tenant. Tenant scoping is the
/// global query filter on <c>StrgDbContext.Users</c>; no explicit tenant predicate is added.
/// The endpoint clamps <see cref="PageSize"/> at the wire layer; the handler re-clamps as
/// defence-in-depth so a programmatic Mediator caller (future internal feature, GraphQL
/// adapter, etc.) cannot exceed the ceiling either. Mirrors
/// <see cref="Strg.Application.Features.Files.List.ListFilesQuery"/>'s pagination contract.
/// </summary>
public sealed record ListUsersQuery(int Page, int PageSize) : IQuery<ListUsersResult>;
