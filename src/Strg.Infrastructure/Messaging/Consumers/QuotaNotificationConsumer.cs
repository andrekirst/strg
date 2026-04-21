using MassTransit;
using Microsoft.Extensions.Logging;
using Strg.Core.Events;

namespace Strg.Infrastructure.Messaging.Consumers;

/// <summary>
/// Placeholder for STRG-063 (quota warnings → user notification). v0.1: log-only fallback.
/// Dead-letter handling writes a structured error; real implementation persists a Notification row
/// + emits a GraphQL subscription fan-out.
/// </summary>
public sealed class QuotaNotificationConsumer :
    IConsumer<QuotaWarningEvent>,
    IConsumer<Fault<QuotaWarningEvent>>
{
    private readonly ILogger<QuotaNotificationConsumer> _logger;

    public QuotaNotificationConsumer(ILogger<QuotaNotificationConsumer> logger) => _logger = logger;

    public Task Consume(ConsumeContext<QuotaWarningEvent> context)
    {
        // TODO STRG-063: persist Notification row + publish GraphQL subscription payload.
        _logger.LogInformation(
            "QuotaNotificationConsumer received QuotaWarningEvent: Tenant={TenantId} User={UserId} Used={UsedBytes} Quota={QuotaBytes}",
            context.Message.TenantId, context.Message.UserId,
            context.Message.UsedBytes, context.Message.QuotaBytes);
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<Fault<QuotaWarningEvent>> context)
    {
        _logger.LogError(
            "Dead-letter: QuotaWarningEvent dispatch failed. Tenant={TenantId} User={UserId} Exceptions={@Exceptions}",
            context.Message.Message.TenantId, context.Message.Message.UserId, context.Message.Exceptions);
        return Task.CompletedTask;
    }
}
