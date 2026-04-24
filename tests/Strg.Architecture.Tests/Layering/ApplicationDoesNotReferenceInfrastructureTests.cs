using FluentAssertions;
using Strg.Application.Behaviors;
using Xunit;

namespace Strg.Architecture.Tests.Layering;

/// <summary>
/// Pins the Clean Architecture layering for Strg.Application: Application sits ABOVE
/// Strg.Infrastructure. Infrastructure depends on Application (to implement IStrgDbContext
/// and other ports), never the other way around. If Strg.Application ever grows a dependency
/// on Strg.Infrastructure, the CQRS foundation's seam between orchestration (Application) and
/// adapters (Infrastructure) collapses and handlers end up coupled to EF Core internals,
/// MassTransit wire types, or HttpContext.
/// </summary>
public sealed class ApplicationDoesNotReferenceInfrastructureTests
{
    [Fact]
    public void Strg_Application_does_not_reference_Strg_Infrastructure()
    {
        var applicationAssembly = typeof(AuditBehavior<,>).Assembly;

        var referenced = applicationAssembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToList();

        referenced.Should().NotContain(
            "Strg.Infrastructure",
            because: "Strg.Application is the orchestration layer; Strg.Infrastructure is its " +
                     "adapter layer. The arrow is Infrastructure → Application (infra implements " +
                     "Application's ports). Reversing it couples handlers to EF internals and " +
                     "undoes the main architectural reason Phase 1 introduced Strg.Application.");
    }
}
