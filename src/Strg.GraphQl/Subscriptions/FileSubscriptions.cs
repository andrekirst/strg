using HotChocolate.Authorization;
using HotChocolate.Execution;
using HotChocolate.Subscriptions;
using Strg.Core.Events;
using Strg.GraphQl.Consumers;
using Strg.GraphQl.Subscriptions.Payloads;

namespace Strg.GraphQl.Subscriptions;

/// <summary>
/// Exposes the <c>fileEvents(driveId: ID!)</c> GraphQL subscription. Events published via
/// <see cref="GraphQlSubscriptionPublisher"/> (STRG-065) are routed per-(tenant, drive)
/// at the topic layer and re-checked per-event at the resolver layer before a payload reaches
/// the subscriber.
/// </summary>
/// <remarks>
/// <para><b>Trust model (subscribe-time vs deliver-time auth).</b> The
/// <see cref="AuthorizeAttribute"/> on <see cref="FileEvents"/> is the subscribe-time gate: it
/// runs against the caller's JWT during the WebSocket handshake and establishes the
/// <c>files.read</c> scope. It does <i>not</i> verify that the caller's tenant owns the requested
/// <c>driveId</c> — drive-ownership is enforced structurally by the topic key, which is keyed on
/// the caller's <b>ambient tenantId</b> (from <c>[GlobalState("tenantId")]</c>). A caller
/// subscribing with a leaked foreign-tenant driveId ends up bound to
/// <c>file-events:{own-tenant}:{foreign-drive}</c>, which is a topic nothing ever publishes to —
/// the channel is structurally empty. This closes the timing/cadence/error-log oracle that a
/// receiver-side-only filter (driveId-keyed topic + per-event resolver reject) would have exposed;
/// see the detailed rationale on <see cref="Topics.FileEvents"/>.</para>
///
/// <para>The resolver-side guard (<c>fileEvent.TenantId != tenantId</c> throws
/// <see cref="UnauthorizedAccessException"/>) is kept as defence-in-depth against a future bug
/// that bypasses the topic key: a custom <see cref="SubscribeToFileEventsAsync"/> impl with regex
/// routing, broken <c>[GlobalState]</c> propagation, or an accidental topic-key downgrade to
/// driveId-only. The regression test
/// <c>FileEvents_throws_UnauthorizedAccessException_when_subscriber_tenant_does_not_match_event_tenant</c>
/// names this invariant and pins it by constructing the cross-tenant scenario directly at the
/// <see cref="ITopicEventSender"/> layer.</para>
///
/// <para><b>Snapshot vs live authorization.</b> The resolver re-checks the tenant on every event,
/// so authorization tracks the event payload, not a snapshot captured at subscribe-time. Drives
/// do not cross tenants today (the domain has no move-drive-between-tenants operation), so the
/// distinction is currently moot. If a future feature allows drive transfer, the topic-key
/// invariant (events published under the new tenant key) carries the authorization transfer
/// atomically — subscribers of the old tenant stop receiving events at the moment the publisher
/// switches tenants on the source event.</para>
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
        // Defence-in-depth: the topic key is keyed on (tenantId, driveId) so cross-tenant events
        // should not reach this resolver at all. This guard pins the invariant against any future
        // regression in the topic-routing layer (see Topics.FileEvents xmldoc).
        if (fileEvent.TenantId != tenantId)
        {
            throw new UnauthorizedAccessException("Subscription event tenant mismatch.");
        }

        return new FileEventPayload(fileEvent.EventType, fileEvent.FileId, fileEvent.DriveId, fileEvent.OccurredAt);
    }

    public ValueTask<ISourceStream<FileEvent>> SubscribeToFileEventsAsync(
        Guid driveId,
        [GlobalState("tenantId")] Guid tenantId,
        [Service] ITopicEventReceiver receiver,
        CancellationToken cancellationToken)
        => receiver.SubscribeAsync<FileEvent>(Topics.FileEvents(tenantId, driveId), cancellationToken);
}
