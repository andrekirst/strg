using FluentAssertions;
using Strg.Plugin.Abstractions;
using Xunit;

namespace Strg.Architecture.Tests.Layering;

/// <summary>
/// TC-001 from STRG-088. Strg.Plugin.Abstractions sits at the bottom of the dependency graph —
/// it is the contract layer plugin authors compile against. A back-edge to any other Strg.*
/// assembly would force every plugin author to drag the host's domain model into their compile
/// graph and would re-introduce the cycle the project split was created to prevent.
/// </summary>
public sealed class PluginAbstractionsHasNoStrgReferencesTests
{
    [Fact]
    public void Strg_Plugin_Abstractions_does_not_reference_any_other_Strg_assembly()
    {
        var assembly = typeof(IStrgPlugin).Assembly;

        var strgRefs = assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null && n.StartsWith("Strg.", StringComparison.Ordinal))
            .ToList();

        strgRefs.Should().BeEmpty(
            because: "Strg.Plugin.Abstractions is the contract layer. A Strg.* back-edge " +
                     "would couple plugin authors to the host's internal types and " +
                     "re-introduce the cycle the split exists to prevent.");
    }
}
