using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Strg.Architecture.Tests.Packaging;

/// <summary>
/// Pins the exact set of NuGet packages Strg.Application takes. Growth beyond this set is a
/// signal worth pausing on: either the Application layer is absorbing a responsibility that
/// belongs in Strg.Infrastructure (HTTP clients, crypto, storage providers), or the project
/// needs a fresh architectural look. Relaxing the test is the checkpoint — the reviewer must
/// add the new package to the allow-list here with a reason.
///
/// <para>The check reads the csproj XML directly rather than the loaded assembly graph because
/// the purpose is to catch intent-at-source (someone adding a PackageReference), not runtime
/// transitive bloat. Transitive NuGet pulls are policed by <see cref="ForbiddenTransitiveDependenciesTests"/>.</para>
/// </summary>
public sealed class ApplicationHasExpectedPackageReferencesTests
{
    // Phase 1 dep set. Each entry is here for a narrow reason; adding to this list should be
    // the rare, reviewed case, not the default move.
    private static readonly string[] ExpectedPackages =
    [
        "Mediator.Abstractions",                   // IRequest / IPipelineBehavior / IMessage
        "Mediator.SourceGenerator",                // compile-time IMediator generation
        "FluentValidation",                        // AbstractValidator<T>
        "FluentValidation.DependencyInjectionExtensions", // AddValidatorsFromAssembly
        "Microsoft.EntityFrameworkCore",           // DbSet<T> on IStrgDbContext
        "MassTransit",                             // IPublishEndpoint for outbox events from handlers
    ];

    [Fact]
    public void Strg_Application_csproj_pins_the_expected_set_of_package_references()
    {
        var csprojPath = Path.Combine(RepoPath.Root, "src", "Strg.Application", "Strg.Application.csproj");
        var doc = XDocument.Load(csprojPath);

        var actualPackages = doc.Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(name => !string.IsNullOrEmpty(name))
            .Cast<string>()
            .ToArray();

        actualPackages.Should().BeEquivalentTo(
            ExpectedPackages,
            because: "any new PackageReference in Strg.Application must first pass a layering " +
                     "review — adding the package to ExpectedPackages here, with its reason, is " +
                     "how that review is recorded. An undeclared addition likely means the " +
                     "Application layer is absorbing responsibility that belongs in Infrastructure.");
    }
}
