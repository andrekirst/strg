using FluentAssertions;
using Xunit;

namespace Strg.Architecture.Tests.Messaging;

/// <summary>
/// Pins that <c>Strg.Api/Program.cs</c> registers
/// <see cref="Strg.GraphQL.Consumers.GraphQLSubscriptionPublisher"/> via the
/// <c>configureConsumers</c> callback passed to
/// <c>AddStrgMassTransit</c> (STRG-065 audit INFO-3). The layering forbids
/// Strg.Infrastructure referencing Strg.GraphQL
/// (see <see cref="Layering.InfrastructureDoesNotReferenceGraphQLTests"/>), so the publisher
/// cannot be registered inside <c>MassTransitExtensions.AddStrgMassTransit</c>; Program.cs is
/// the only place where the one-directional dependency allows the wire-up. A refactor that
/// drops the callback argument silently turns off every GraphQL subscription notification —
/// the wire protocol keeps working, but live subscribers never get the push. This test fails
/// loudly when the registration disappears.
///
/// <para><b>Why source-text.</b> The runtime alternative is booting
/// <c>WebApplicationFactory&lt;Program&gt;</c> and inspecting service descriptors, which drags
/// in Postgres + RabbitMQ fixtures. The invariant we care about *is* a specific line in
/// Program.cs — pinning the source directly is cheaper and less brittle than pinning a
/// downstream DI consequence of that line.</para>
/// </summary>
public sealed class GraphQLSubscriptionPublisherRegistrationTests
{
    [Fact]
    public void Program_cs_registers_GraphQLSubscriptionPublisher_via_configureConsumers_callback()
    {
        var source = RepoPath.Read("src/Strg.Api/Program.cs");

        source.Should().Contain(
            "AddStrgMassTransit(",
            because: "Program.cs must call AddStrgMassTransit — removing it would drop the " +
                     "entire outbox + consumer wiring, not just the GraphQL publisher");

        // Full-qualified reference: Strg.Infrastructure cannot `using Strg.GraphQL`, so
        // Program.cs references the publisher by its fully-qualified name. The substring
        // check tolerates whitespace/newline variations in the callback formatting.
        source.Should().Contain(
            "AddConsumer<Strg.GraphQL.Consumers.GraphQLSubscriptionPublisher>",
            because: "the GraphQL bridge consumer must be registered in the MassTransit bus " +
                     "callback from Strg.Api — it cannot live in Strg.Infrastructure's " +
                     "registration because that would invert the layer dependency " +
                     "(Infrastructure → GraphQL). See the xmldoc on AddStrgMassTransit's " +
                     "configureConsumers parameter.");
    }
}
