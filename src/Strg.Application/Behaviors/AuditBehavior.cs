using System.Reflection;
using Mediator;
using Microsoft.Extensions.Logging;
using Strg.Application.Abstractions;
using Strg.Application.Auditing;
using Strg.Core;
using Strg.Core.Auditing;

namespace Strg.Application.Behaviors;

/// <summary>
/// Post-success audit writer for commands marked <see cref="IAuditedCommand"/>. The handler
/// populates the scoped <see cref="IAuditScope"/>; this behavior reads it after the handler
/// returns, verifies the response represents success (non-Result responses are treated as
/// success), and writes the entry via <see cref="IAuditService"/>. Audit failures swallow
/// (logged warning) so the primary op never fails because the audit log did. Cancellation
/// propagates unchanged.
/// </summary>
public sealed class AuditBehavior<TMessage, TResponse>(
    IAuditScope auditScope,
    IAuditService auditService,
    ILogger<AuditBehavior<TMessage, TResponse>> logger)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    // Evaluated once per closed TResponse at JIT time. Null = TResponse is not a Result shape →
    // treat every response as success. Mirrors ValidationBehavior.BuildResultFailureFactory.
    private static readonly Func<TResponse, bool>? IsSuccessReader = BuildIsSuccessReader();

    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await next(message, cancellationToken);

            if (message is not IAuditedCommand || !auditScope.IsPopulated)
            {
                return response;
            }

            if (IsSuccessReader is not null && !IsSuccessReader(response))
            {
                return response;
            }

            var entry = auditScope.BuildEntry();
            if (entry is null)
            {
                return response;
            }

            try
            {
                await auditService.LogAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException)
                {
                    throw;
                }
                logger.LogWarning(
                    ex,
                    "Audit write failed post-handler: action={Action} resourceType={ResourceType} resourceId={ResourceId} userId={UserId}",
                    entry.Action, entry.ResourceType, entry.ResourceId, entry.UserId);
            }

            return response;
        }
        finally
        {
            // Clear the scope so it is ready for the next command. A single DI scope can dispatch
            // multiple commands (integration tests, hosted services); without this reset, the
            // double-Record guard inside AuditScope fires on the second command in the scope.
            auditScope.Reset();
        }
    }

    private static Func<TResponse, bool>? BuildIsSuccessReader()
    {
        var responseType = typeof(TResponse);

        if (responseType == typeof(Result))
        {
            return static r => ((Result)(object)r!).IsSuccess;
        }

        if (responseType.IsGenericType && responseType.GetGenericTypeDefinition() == typeof(Result<>))
        {
            var prop = responseType.GetProperty(
                nameof(Result<object>.IsSuccess),
                BindingFlags.Public | BindingFlags.Instance);

            if (prop is null)
            {
                return null;
            }

            return r => (bool)prop.GetValue(r)!;
        }

        return null;
    }
}
