using FluentAssertions;
using Xunit;

namespace Strg.Architecture.Tests.Messaging;

/// <summary>
/// Pins that <c>MassTransitExtensions.AddStrgMassTransit</c> wires the EF-Core outbox with
/// <c>UseBusOutbox()</c> (STRG-064 audit Corollary-2). That single call is what causes
/// <see cref="MassTransit.IPublishEndpoint"/> to resolve to the outbox-wrapped variant in the
/// production DI container — without it, publishes go straight to the transport and bypass
/// the outbox table, re-opening the dual-write problem that STRG-061 exists to solve.
///
/// <para><b>Why source-text rather than runtime-DI.</b> The runtime alternative is to build a
/// <c>ServiceProvider</c> via <c>AddStrgMassTransit</c>, resolve <c>IPublishEndpoint</c>, and
/// assert the concrete type contains "Outbox". That works but pulls in a live DbContext + a
/// RabbitMQ-shaped configuration just to observe a single wiring call. The source-text check
/// pins the exact line the outbox contract depends on, at a fraction of the boot cost, and
/// fails with a clear diff when the line is deleted or mistyped. The cost is that a renamed
/// MassTransit API (e.g. if a future major renames <c>UseBusOutbox</c>) would false-positive
/// this test even if the underlying behavior is preserved — which is the right side of the
/// trade-off for an ArchTest that exists precisely to catch quiet removals.</para>
/// </summary>
public sealed class OutboxWrappedPublishEndpointTests
{
    [Fact]
    public void AddStrgMassTransit_calls_UseBusOutbox_on_the_EntityFrameworkOutbox()
    {
        var source = RepoPath.Read("src/Strg.Infrastructure/Messaging/MassTransitExtensions.cs");

        source.Should().Contain(
            "AddEntityFrameworkOutbox<StrgDbContext>",
            because: "the EF-Core outbox on StrgDbContext is the storage substrate for " +
                     "atomic event persistence — removing it breaks the dual-write contract");

        source.Should().Contain(
            "outbox.UseBusOutbox();",
            because: "UseBusOutbox() is what makes IPublishEndpoint resolve to the " +
                     "outbox-wrapped variant. Without it, publishes bypass the outbox " +
                     "table and go straight to the transport — at-least-once delivery " +
                     "across a crash between DB commit and broker publish is lost.");
    }
}
