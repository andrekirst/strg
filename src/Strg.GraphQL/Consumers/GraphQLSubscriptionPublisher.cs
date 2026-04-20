using HotChocolate.Subscriptions;
using MassTransit;
using Microsoft.Extensions.Logging;
using Strg.Core.Events;

namespace Strg.GraphQL.Consumers;

public sealed class GraphQLSubscriptionPublisher :
    IConsumer<FileUploadedEvent>,
    IConsumer<FileDeletedEvent>,
    IConsumer<FileMovedEvent>,
    IConsumer<FileCopiedEvent>,
    IConsumer<FileRenamedEvent>
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

    private async Task SendAsync(
        FileEventType type, Guid fileId, Guid driveId, Guid userId, Guid tenantId,
        string? oldPath, string? newPath, CancellationToken ct)
    {
        var evt = new FileEvent(type, fileId, driveId, userId, tenantId, oldPath, newPath, DateTimeOffset.UtcNow);
        await _sender.SendAsync(Topics.FileEvents(driveId), evt, ct);
        _logger.LogDebug("Published {EventType} event to topic {Topic}", type, Topics.FileEvents(driveId));
    }
}
