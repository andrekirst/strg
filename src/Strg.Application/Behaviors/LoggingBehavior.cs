using System.Diagnostics;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Strg.Application.Behaviors;

public sealed class LoggingBehavior<TMessage, TResponse>(ILogger<LoggingBehavior<TMessage, TResponse>> logger)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        var messageName = typeof(TMessage).Name;
        var stopwatch = Stopwatch.StartNew();
        logger.LogDebug("Mediator {MessageName} starting", messageName);
        try
        {
            var response = await next(message, cancellationToken);
            logger.LogDebug(
                "Mediator {MessageName} completed in {ElapsedMs}ms",
                messageName,
                stopwatch.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Mediator {MessageName} threw after {ElapsedMs}ms",
                messageName,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
