using HotChocolate.Subscriptions;
using MassTransit;
using Microsoft.Extensions.Logging;
using Strg.Core.Domain;
using Strg.Core.Events;
using Strg.GraphQL.Subscriptions.Payloads;

namespace Strg.GraphQL.Consumers;

public sealed class GraphQLSubscriptionPublisher :
    IConsumer<FileUploadedEvent>,
    IConsumer<FileDeletedEvent>,
    IConsumer<FileMovedEvent>,
    IConsumer<FileCopiedEvent>,
    IConsumer<FileRenamedEvent>,
    IConsumer<QuotaWarningEvent>
{
    private readonly ITopicEventSender _sender;
    private readonly ILogger<GraphQLSubscriptionPublisher> _logger;

    public GraphQLSubscriptionPublisher(ITopicEventSender sender, ILogger<GraphQLSubscriptionPublisher> logger)
    {
        _sender = sender;
        _logger = logger;
    }

    public Task Consume(ConsumeContext<FileUploadedEvent> ctx)
        => SendAsync(FileEventType.Uploaded, ctx.Message.FileId, ctx.Message.DriveId,
            ctx.Message.UserId, ctx.Message.TenantId, null, null, ctx.CancellationToken);

    public Task Consume(ConsumeContext<FileDeletedEvent> ctx)
        => SendAsync(FileEventType.Deleted, ctx.Message.FileId, ctx.Message.DriveId,
            ctx.Message.UserId, ctx.Message.TenantId, null, null, ctx.CancellationToken);

    public Task Consume(ConsumeContext<FileMovedEvent> ctx)
        => SendAsync(FileEventType.Moved, ctx.Message.FileId, ctx.Message.DriveId,
            ctx.Message.UserId, ctx.Message.TenantId, ctx.Message.OldPath, ctx.Message.NewPath, ctx.CancellationToken);

    public Task Consume(ConsumeContext<FileCopiedEvent> ctx)
        => SendAsync(FileEventType.Copied, ctx.Message.FileId, ctx.Message.DriveId,
            ctx.Message.UserId, ctx.Message.TenantId, null, ctx.Message.NewPath, ctx.CancellationToken);

    public Task Consume(ConsumeContext<FileRenamedEvent> ctx)
        => SendAsync(FileEventType.Renamed, ctx.Message.FileId, ctx.Message.DriveId,
            ctx.Message.UserId, ctx.Message.TenantId, ctx.Message.OldName, ctx.Message.NewName, ctx.CancellationToken);

    public async Task Consume(ConsumeContext<QuotaWarningEvent> ctx)
    {
        var msg = ctx.Message;
        var ratio = msg.QuotaBytes <= 0 ? 0d : (double)msg.UsedBytes / msg.QuotaBytes;
        // Mirrors QuotaNotificationConsumer's level derivation so the persistent Notification
        // row and the live subscription payload agree on the discriminator for the same event.
        var level = ratio >= QuotaThresholds.Critical
            ? QuotaThresholds.CriticalLevel
            : QuotaThresholds.WarningLevel;

        var payload = new QuotaWarningPayload(level, msg.UsedBytes, msg.QuotaBytes, DateTimeOffset.UtcNow);
        var topic = Topics.QuotaWarnings(msg.TenantId, msg.UserId);
        await _sender.SendAsync(topic, payload, ctx.CancellationToken);
        _logger.LogDebug("Published QuotaWarning ({Level}) to topic {Topic}", level, topic);
    }

    private async Task SendAsync(
        FileEventType type, Guid fileId, Guid driveId, Guid userId, Guid tenantId,
        string? oldPath, string? newPath, CancellationToken cancellationToken)
    {
        var evt = new FileEvent(type, fileId, driveId, userId, tenantId, oldPath, newPath, DateTimeOffset.UtcNow);
        var topic = Topics.FileEvents(tenantId, driveId);
        await _sender.SendAsync(topic, evt, cancellationToken);
        _logger.LogDebug("Published {EventType} event to topic {Topic}", type, topic);
    }
}
