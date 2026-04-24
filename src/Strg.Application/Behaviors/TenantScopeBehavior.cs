using Mediator;
using Strg.Application.Abstractions;
using Strg.Core.Domain;

namespace Strg.Application.Behaviors;

public sealed class TenantScopeBehavior<TMessage, TResponse>(ITenantContext tenantContext)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    public ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        if (message is ITenantScopedCommand && tenantContext.TenantId == Guid.Empty)
        {
            throw new UnauthorizedAccessException(
                $"{typeof(TMessage).Name} is tenant-scoped but no tenant is bound to the current request.");
        }

        return next(message, cancellationToken);
    }
}
