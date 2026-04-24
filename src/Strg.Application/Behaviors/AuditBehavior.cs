using Mediator;
using Microsoft.Extensions.Logging;
using Strg.Application.Abstractions;

namespace Strg.Application.Behaviors;

/// <summary>
/// Post-success hook for commands tagged with <see cref="IAuditedCommand"/>. The current
/// implementation logs the event — feature migrations in Phase 2+ replace this scaffold with
/// <c>IAuditService.LogAsync</c> writes once each command declares the specific
/// <c>AuditEntry</c> shape it wants persisted. Running now (rather than after migration)
/// exercises the pipeline wiring end-to-end from Phase 1.
/// </summary>
public sealed class AuditBehavior<TMessage, TResponse>(ILogger<AuditBehavior<TMessage, TResponse>> logger)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        var response = await next(message, cancellationToken);

        if (message is IAuditedCommand)
        {
            logger.LogInformation("Audit: {MessageType} completed", typeof(TMessage).Name);
        }

        return response;
    }
}
