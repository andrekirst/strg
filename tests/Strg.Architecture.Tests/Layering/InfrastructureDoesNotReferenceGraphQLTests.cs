using FluentAssertions;
using Strg.Infrastructure.Data;
using Xunit;

namespace Strg.Architecture.Tests.Layering;

/// <summary>
/// Pins the v0.1 layer-direction rule from CLAUDE.md: Strg.Infrastructure depends on Strg.Core
/// but must not reach upwards into Strg.GraphQL. The moment Infrastructure references GraphQL,
/// the ITopicEventSender coupling inverts the layering and makes it impossible to wire
/// <c>GraphQLSubscriptionPublisher</c> from the hosting project (Strg.Api) the way Program.cs
/// currently does — see the callback hook comment in
/// <c>MassTransitExtensions.AddStrgMassTransit</c>.
/// </summary>
public sealed class InfrastructureDoesNotReferenceGraphQlTests
{
    [Fact]
    public void Strg_Infrastructure_does_not_reference_Strg_GraphQL()
    {
        // StrgDbContext is a stable, always-loaded type in Strg.Infrastructure — any type
        // from the target assembly would work, but StrgDbContext is least likely to be deleted
        // or refactored into a different assembly.
        var infrastructureAssembly = typeof(StrgDbContext).Assembly;

        var referenced = infrastructureAssembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToList();

        referenced.Should().NotContain(
            "Strg.GraphQL",
            because: "Strg.Infrastructure must not depend on Strg.GraphQL — see CLAUDE.md " +
                     "layering rules. GraphQL consumers (e.g. GraphQLSubscriptionPublisher) " +
                     "are wired into the MassTransit bus from Strg.Api via the " +
                     "configureConsumers callback on AddStrgMassTransit, precisely so this " +
                     "dependency arrow stays one-way.");
    }
}
