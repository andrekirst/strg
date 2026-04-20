using HotChocolate;
using HotChocolate.Authorization;
using HotChocolate.Execution;
using HotChocolate.Subscriptions;
using HotChocolate.Types;
using Strg.Core.Events;
using Strg.GraphQL.Subscriptions.Payloads;

namespace Strg.GraphQL.Subscriptions;

[ExtendObjectType("Subscription")]
public sealed class FileSubscriptions
{
    [Subscribe(With = nameof(SubscribeToFileEventsAsync))]
    [Authorize(Policy = "FilesRead")]
    public FileEventPayload FileEvents(
        Guid driveId,
        [EventMessage] FileEvent fileEvent,
        [GlobalState("tenantId")] Guid tenantId)
    {
        if (fileEvent.TenantId != tenantId)
        {
            throw new UnauthorizedAccessException("Subscription event tenant mismatch.");
        }

        return new FileEventPayload(fileEvent.EventType, fileEvent.FileId, fileEvent.DriveId, fileEvent.OccurredAt);
    }

    public ValueTask<ISourceStream<FileEvent>> SubscribeToFileEventsAsync(
        Guid driveId,
        [Service] ITopicEventReceiver receiver,
        CancellationToken cancellationToken)
        => receiver.SubscribeAsync<FileEvent>(Topics.FileEvents(driveId), cancellationToken);
}
