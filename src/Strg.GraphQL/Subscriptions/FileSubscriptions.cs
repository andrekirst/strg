using HotChocolate;
using HotChocolate.Authorization;
using HotChocolate.Execution;
using HotChocolate.Subscriptions;
using HotChocolate.Types;
using Strg.Core.Events;
using Strg.GraphQL.Subscriptions.Payloads;

namespace Strg.GraphQL.Subscriptions;

/// <summary>
/// Exposes the <c>fileEvents(driveId: ID!)</c> GraphQL subscription. Events published via
/// <see cref="Consumers.GraphQLSubscriptionPublisher"/> (STRG-065) are filtered per-drive on the
/// topic level and per-tenant on the resolver level before a payload reaches the subscriber.
/// </summary>
/// <remarks>
/// <para><b>Trust model (subscribe-time vs deliver-time auth).</b> The
/// <see cref="AuthorizeAttribute"/> on <see cref="FileEvents"/> is the subscribe-time gate: it
/// runs against the caller's JWT during the WebSocket handshake and establishes the
/// <c>files.read</c> scope. It does <i>not</i> verify that the caller's tenant owns the requested
/// <c>driveId</c> — drive-level tenancy is enforced at deliver-time by the
/// <c>fileEvent.TenantId != tenantId</c> guard inside the resolver. This means a subscriber on
/// a drive outside their tenant will still see messages fan out to their stream handler, but the
/// resolver throws <see cref="UnauthorizedAccessException"/> before the payload is materialised,
/// so nothing crosses the wire. This is intentional: it keeps the subscribe-path cheap
/// (no repository lookup on connect) and moves the check to the single point where data would
/// otherwise leak. If this assumption ever breaks — e.g. events start carrying tenant-scoped
/// payloads at the ISourceStream layer — the guard must move to <see cref="SubscribeToFileEventsAsync"/>.</para>
///
/// <para><b>Snapshot vs live authorization.</b> The resolver re-checks the tenant on every event,
/// so authorization tracks the event's payload, not a snapshot captured at subscribe-time. Drives
/// do not cross tenants today (the domain has no move-drive-between-tenants operation), so the
/// distinction is currently moot, but the contract is "deliver-time tenancy wins" — a future
/// drive-transfer feature will not silently leak events already in flight.</para>
///
/// <para><b>Topic naming (<c>file-events:{driveId}</c>).</b> Per-drive rather than
/// <c>file-events:{tenantId}:{driveId}</c> because drives are tenant-scoped by their
/// <see cref="Core.Domain.Drive.TenantId"/> invariant and cannot be reassigned. A per-drive topic
/// is therefore safe-by-construction: two drives in different tenants cannot collide on
/// <see cref="Guid"/> allocation, so the tenant prefix would be redundant. Contrast with
/// <see cref="Topics.QuotaWarnings"/>, which is per-(tenant, user) because <c>userId</c> alone
/// is theoretically collidable across tenants and defence-in-depth at the topic level is cheap
/// insurance. If drives ever become transferable between tenants, this rationale breaks and the
/// topic must include the tenant prefix — see ArchTest #102 and STRG-065.</para>
/// </remarks>
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
