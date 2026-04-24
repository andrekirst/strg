using FluentAssertions;
using Mediator;
using Strg.Application.Behaviors;
using Xunit;

namespace Strg.Architecture.Tests.Layering;

/// <summary>
/// Pins the VSA (vertical-slice architecture) convention for Strg.Application: every handler
/// that talks to Mediator — <see cref="IRequestHandler{TRequest,TResponse}"/> and its
/// <see cref="IRequestHandler{TRequest}"/> sibling — must live under
/// <c>Strg.Application.Features.*</c>. Co-locating command + handler + validator inside a
/// single feature folder is the whole point of VSA; letting a handler drift into
/// <c>Abstractions/</c>, <c>Behaviors/</c>, or a top-level utility folder silently breaks the
/// pattern before it's had time to prove itself.
/// </summary>
public sealed class ApplicationHandlersLiveInFeaturesTests
{
    [Fact]
    public void All_Mediator_handlers_in_Strg_Application_are_under_Features_namespace()
    {
        var applicationAssembly = typeof(AuditBehavior<,>).Assembly;

        var handlerTypes = applicationAssembly.GetTypes()
            .Where(t => !t.IsInterface && !t.IsAbstract)
            .Where(t => t.GetInterfaces().Any(IsMediatorHandlerInterface))
            .ToList();

        handlerTypes.Should().NotBeEmpty(
            "at least one handler (PingHandler) exists as the Phase 1 smoke canary — if this " +
            "comes back empty the reflection filter is broken, not the architecture");

        foreach (var handler in handlerTypes)
        {
            handler.Namespace.Should().StartWith(
                "Strg.Application.Features.",
                because: $"handler {handler.FullName} must live under a Features/ slice folder so " +
                         $"command + handler + validator stay co-located — Abstractions/, Behaviors/, " +
                         $"and DependencyInjection/ are for infrastructure-of-the-infrastructure, not " +
                         $"for business-logic handlers");
        }
    }

    private static bool IsMediatorHandlerInterface(Type i)
    {
        if (!i.IsGenericType)
        {
            return false;
        }

        var def = i.GetGenericTypeDefinition();
        return def == typeof(IRequestHandler<,>)
            || def == typeof(IRequestHandler<>)
            || def == typeof(ICommandHandler<,>)
            || def == typeof(ICommandHandler<>)
            || def == typeof(IQueryHandler<,>)
            || def == typeof(INotificationHandler<>);
    }
}
