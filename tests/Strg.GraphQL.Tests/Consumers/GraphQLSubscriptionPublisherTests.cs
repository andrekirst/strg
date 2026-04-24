using FluentAssertions;
using HotChocolate.Subscriptions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Strg.Core.Events;
using Strg.GraphQL.Consumers;
using Xunit;

namespace Strg.GraphQL.Tests.Consumers;

/// <summary>
/// Exercises the GraphQLSubscriptionPublisher consumer in isolation: each domain event variant
/// must land on the right <see cref="Topics.FileEvents"/> per-drive topic, carry the correct
/// <see cref="FileEventType"/> discriminator, and propagate <see cref="FileEvent.TenantId"/> so
/// the subscription resolver can enforce tenant isolation (see
/// <see cref="Subscriptions.FileSubscriptions"/>).
///
/// <para>We bypass the MassTransit harness and the Hot Chocolate in-memory backplane here:
/// <see cref="FileSubscriptionsTests"/> already round-trips via the real backplane, so the
/// invariant this file defends is "the consumer fans out correctly," not "the subscription
/// pipeline works." Direct Consume(...) calls with a substituted <see cref="ConsumeContext{T}"/>
/// keep the test surface tight and run in &lt;100ms.</para>
/// </summary>
public sealed class GraphQlSubscriptionPublisherTests
{
    private readonly ITopicEventSender _sender = Substitute.For<ITopicEventSender>();
    private readonly GraphQlSubscriptionPublisher _consumer;

    public GraphQlSubscriptionPublisherTests()
    {
        _consumer = new GraphQlSubscriptionPublisher(_sender, NullLogger<GraphQlSubscriptionPublisher>.Instance);
    }

    // TC-001
    [Fact]
    public async Task FileUploadedEvent_fans_out_to_file_events_topic_with_uploaded_discriminator()
    {
        var driveId = Guid.NewGuid();
        var evt = new FileUploadedEvent(
            TenantId: Guid.NewGuid(),
            FileId: Guid.NewGuid(),
            DriveId: driveId,
            UserId: Guid.NewGuid(),
            Size: 1024,
            MimeType: "image/png");

        await _consumer.Consume(ContextFor(evt));

        await _sender.Received(1).SendAsync(
            Topics.FileEvents(evt.TenantId, driveId),
            Arg.Is<FileEvent>(fe =>
                fe.EventType == FileEventType.Uploaded
                && fe.FileId == evt.FileId
                && fe.DriveId == driveId
                && fe.UserId == evt.UserId
                && fe.TenantId == evt.TenantId
                && fe.OldPath == null
                && fe.NewPath == null),
            Arg.Any<CancellationToken>());
    }

    // TC-002
    [Fact]
    public async Task FileDeletedEvent_fans_out_with_deleted_discriminator_and_no_paths()
    {
        var driveId = Guid.NewGuid();
        var evt = new FileDeletedEvent(
            TenantId: Guid.NewGuid(),
            FileId: Guid.NewGuid(),
            DriveId: driveId,
            UserId: Guid.NewGuid());

        await _consumer.Consume(ContextFor(evt));

        await _sender.Received(1).SendAsync(
            Topics.FileEvents(evt.TenantId, driveId),
            Arg.Is<FileEvent>(fe => fe.EventType == FileEventType.Deleted && fe.OldPath == null && fe.NewPath == null),
            Arg.Any<CancellationToken>());
    }

    // TC-003
    [Fact]
    public async Task FileMovedEvent_propagates_old_and_new_paths()
    {
        var driveId = Guid.NewGuid();
        var evt = new FileMovedEvent(
            TenantId: Guid.NewGuid(),
            FileId: Guid.NewGuid(),
            DriveId: driveId,
            OldPath: "/docs/old.txt",
            NewPath: "/archive/old.txt",
            UserId: Guid.NewGuid());

        await _consumer.Consume(ContextFor(evt));

        await _sender.Received(1).SendAsync(
            Topics.FileEvents(evt.TenantId, driveId),
            Arg.Is<FileEvent>(fe =>
                fe.EventType == FileEventType.Moved
                && fe.OldPath == "/docs/old.txt"
                && fe.NewPath == "/archive/old.txt"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FileCopiedEvent_sets_copied_discriminator_and_NewPath_only()
    {
        // Copy differs from Move: there's no meaningful OldPath on the new entity — it's a fresh
        // FileItem at NewPath. FileCopiedEvent carries only NewPath, and the publisher maps that
        // onto FileEvent.NewPath with OldPath = null. Asserts the shape so a future refactor
        // that accidentally wires OldPath = source path breaks this test.
        var driveId = Guid.NewGuid();
        var evt = new FileCopiedEvent(
            FileId: Guid.NewGuid(),
            DriveId: driveId,
            UserId: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            NewPath: "/copy/dest.txt");

        await _consumer.Consume(ContextFor(evt));

        await _sender.Received(1).SendAsync(
            Topics.FileEvents(evt.TenantId, driveId),
            Arg.Is<FileEvent>(fe =>
                fe.EventType == FileEventType.Copied
                && fe.OldPath == null
                && fe.NewPath == "/copy/dest.txt"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FileRenamedEvent_maps_OldName_and_NewName_into_OldPath_and_NewPath()
    {
        // Rename is a name-only change — the parent folder is unchanged and FileRenamedEvent
        // carries (OldName, NewName) rather than paths. The subscription contract only exposes
        // OldPath/NewPath, so the publisher reuses those slots. Defends against someone dropping
        // the names on the floor because the FileEvent DTO has no OldName/NewName fields.
        var driveId = Guid.NewGuid();
        var evt = new FileRenamedEvent(
            FileId: Guid.NewGuid(),
            DriveId: driveId,
            UserId: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            OldName: "draft.txt",
            NewName: "final.txt");

        await _consumer.Consume(ContextFor(evt));

        await _sender.Received(1).SendAsync(
            Topics.FileEvents(evt.TenantId, driveId),
            Arg.Is<FileEvent>(fe =>
                fe.EventType == FileEventType.Renamed
                && fe.OldPath == "draft.txt"
                && fe.NewPath == "final.txt"),
            Arg.Any<CancellationToken>());
    }

    // TC-005 — pins the topic string shape shared with FileSubscriptions.SubscribeToFileEventsAsync.
    // Hardcoded format keeps the test from circular-referencing the Topics helper it is defending.
    // If someone renames the topic convention in one place and forgets the other, this fires.
    // Tenant-prefix is load-bearing: a receiver-side-only filter (driveId-keyed) would expose a
    // timing/cadence/error-log oracle to a cross-tenant subscriber holding a leaked driveId. See
    // Topics.FileEvents xmldoc for the full rationale.
    [Fact]
    public void Topics_FileEvents_format_matches_subscription_receiver_contract()
    {
        var tenantId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
        var driveId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Topics.FileEvents(tenantId, driveId).Should()
            .Be("file-events:00000000-0000-0000-0000-0000000000aa:00000000-0000-0000-0000-000000000001");
    }

    private static ConsumeContext<T> ContextFor<T>(T message) where T : class
    {
        var ctx = Substitute.For<ConsumeContext<T>>();
        ctx.Message.Returns(message);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }
}
