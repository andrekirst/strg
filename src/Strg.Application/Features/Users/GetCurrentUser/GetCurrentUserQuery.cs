using Mediator;
using Strg.Core.Domain;

namespace Strg.Application.Features.Users.GetCurrentUser;

/// <summary>
/// STRG-059 — returns the current authenticated user's row, or <see langword="null"/> when the
/// user record has been soft-deleted (token still valid but the row is gone). The user id is
/// read from <see cref="ICurrentUser"/> inside the handler — keeping it off the wire prevents a
/// privilege-escalation oracle where a caller substitutes another user's id and reads their
/// profile through the <c>/me</c> shortcut. The REST endpoint maps a <see langword="null"/>
/// result to HTTP 404.
/// </summary>
public sealed record GetCurrentUserQuery() : IQuery<User?>;
