using Mediator;
using Strg.Core;

namespace Strg.Application.Features.Ping;

/// <summary>
/// Phase 1 smoke-test command: exercises the Mediator dispatcher and every pipeline behavior
/// end-to-end without touching the database. Migrations in Phase 2+ add real commands; this one
/// stays as a canary that the foundation still works.
/// </summary>
public sealed record PingCommand(string Message) : ICommand<Result<PingResponse>>;

public sealed record PingResponse(string Echo, DateTimeOffset ReceivedAt);
