using FluentAssertions;
using Xunit;

namespace Strg.Architecture.Tests.Messaging;

/// <summary>
/// Pins that <c>Strg.Api/Program.cs</c> registers
/// <see cref="Strg.WebDav.Consumers.WebDavJwtCacheInvalidationConsumer"/> via the
/// <c>configureConsumers</c> callback passed to <c>AddStrgMassTransit</c> (STRG-073 Commit 3).
/// Mirrors <see cref="GraphQlSubscriptionPublisherRegistrationTests"/> for the sibling-layer
/// reason: Strg.Infrastructure cannot reference Strg.WebDav without inverting the layer
/// dependency, so the consumer can only be registered at the Strg.Api composition root.
///
/// <para>A refactor that drops the registration silently turns off outbox-driven WebDAV JWT
/// cache invalidation. The symptom is not a crash — it is stale cache entries continuing to
/// serve the OLD password for up to the 14-min JWT TTL after a password change, reintroducing
/// the window the consumer exists to close. A test of "the registration exists" is the only
/// cheap defense against that silent regression.</para>
///
/// <para><b>Why source-text.</b> Same reasoning as the GraphQL counterpart: booting
/// <c>WebApplicationFactory&lt;Program&gt;</c> drags in Postgres + RabbitMQ fixtures, and the
/// invariant IS a specific line in Program.cs — pinning the source directly is cheaper and less
/// brittle than pinning a downstream DI consequence.</para>
/// </summary>
public sealed class WebDavJwtCacheInvalidationConsumerRegistrationTests
{
    [Fact]
    public void Program_cs_registers_WebDavJwtCacheInvalidationConsumer_via_configureConsumers_callback()
    {
        var source = RepoPath.Read("src/Strg.Api/Program.cs");

        source.Should().Contain(
            "AddStrgMassTransit(",
            because: "Program.cs must call AddStrgMassTransit — removing it would drop the " +
                     "entire outbox + consumer wiring, not just the WebDAV cache consumer");

        // Full-qualified reference: Strg.Infrastructure cannot `using Strg.WebDav`, so
        // Program.cs references the consumer by its fully-qualified name. The substring
        // check tolerates whitespace/newline variations in the callback formatting.
        source.Should().Contain(
            "AddConsumer<Strg.WebDav.Consumers.WebDavJwtCacheInvalidationConsumer>",
            because: "the WebDAV JWT cache invalidation consumer must be registered in the " +
                     "MassTransit bus callback from Strg.Api — it cannot live in " +
                     "Strg.Infrastructure's registration because that would invert the layer " +
                     "dependency (Infrastructure → WebDav). Without the registration, " +
                     "UserPasswordChangedEvent is published into the outbox but no consumer " +
                     "evicts the cached Basic-Auth → JWT exchange, and the old password " +
                     "continues to authenticate for up to the 14-minute JWT cache TTL.");
    }
}
