using Mediator;
using Strg.Core;
using Strg.Core.Domain;

namespace Strg.Application.Features.Users.UpdateProfile;

/// <summary>
/// STRG-059 — updates the CURRENT user's <see cref="User.DisplayName"/>. The user id is read
/// from <see cref="ICurrentUser"/> inside the handler so wire callers cannot substitute another
/// user's id.
///
/// <para>Returns <see cref="Result{T}"/> over <see cref="User"/> so validation failures (empty
/// or whitespace display name) surface via <c>ErrorCode = "ValidationError"</c> from the
/// validation pipeline behavior — the REST shim maps them to HTTP 400. Without this Result
/// shape the behavior throws <c>StrgValidationException</c>, which has no global REST mapping
/// and would surface as HTTP 500.</para>
/// </summary>
public sealed record UpdateProfileCommand(string DisplayName) : ICommand<Result<User>>;
