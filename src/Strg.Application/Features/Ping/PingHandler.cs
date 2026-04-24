using Mediator;
using Strg.Core;

namespace Strg.Application.Features.Ping;

internal sealed class PingHandler : ICommandHandler<PingCommand, Result<PingResponse>>
{
    public ValueTask<Result<PingResponse>> Handle(PingCommand command, CancellationToken cancellationToken)
        => ValueTask.FromResult(
            Result<PingResponse>.Success(
                new PingResponse($"pong: {command.Message}", DateTimeOffset.UtcNow)));
}
