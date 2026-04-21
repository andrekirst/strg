using MassTransit;
using Microsoft.Extensions.Logging;
using Strg.Core.Events;

namespace Strg.Infrastructure.Messaging.Consumers;

/// <summary>
/// Placeholder for STRG-064 (GraphQL subscription fan-out). v0.1: log-only fallback.
/// Real implementation bridges file lifecycle events into Hot Chocolate's ITopicEventSender so
/// connected clients receive real-time file tree updates.
/// </summary>
public sealed class GraphQLSubscriptionPublisher :
    IConsumer<FileUploadedEvent>,
    IConsumer<FileDeletedEvent>,
    IConsumer<FileMovedEvent>,
    IConsumer<QuotaWarningEvent>,
    IConsumer<BackupCompletedEvent>
{
    private readonly ILogger<GraphQLSubscriptionPublisher> _logger;

    public GraphQLSubscriptionPublisher(ILogger<GraphQLSubscriptionPublisher> logger) => _logger = logger;

    public Task Consume(ConsumeContext<FileUploadedEvent> context)
    {
        // TODO STRG-064: ITopicEventSender.SendAsync($"file-uploaded-{TenantId}", payload).
        _logger.LogDebug("GraphQLSubscriptionPublisher: FileUploaded {FileId}", context.Message.FileId);
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<FileDeletedEvent> context)
    {
        _logger.LogDebug("GraphQLSubscriptionPublisher: FileDeleted {FileId}", context.Message.FileId);
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<FileMovedEvent> context)
    {
        _logger.LogDebug("GraphQLSubscriptionPublisher: FileMoved {FileId}", context.Message.FileId);
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<QuotaWarningEvent> context)
    {
        _logger.LogDebug("GraphQLSubscriptionPublisher: QuotaWarning for user {UserId}", context.Message.UserId);
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<BackupCompletedEvent> context)
    {
        _logger.LogDebug("GraphQLSubscriptionPublisher: BackupCompleted for drive {DriveId}", context.Message.DriveId);
        return Task.CompletedTask;
    }
}
