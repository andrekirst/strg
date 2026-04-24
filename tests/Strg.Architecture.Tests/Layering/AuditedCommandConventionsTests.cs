using FluentAssertions;
using Mediator;
using Strg.Application.Abstractions;
using Strg.Application.Auditing;
using Strg.Application.Behaviors;
using Xunit;

namespace Strg.Architecture.Tests.Layering;

/// <summary>
/// Pins two invariants of the IAuditScope + AuditBehavior wiring:
/// <list type="number">
///   <item>Every <see cref="IAuditedCommand"/> also implements <see cref="ITenantScopedCommand"/>,
///   so <see cref="Behaviors.TenantScopeBehavior{TMessage,TResponse}"/> rejects unauthenticated
///   callers before <see cref="AuditScope.BuildEntry"/> resolves <c>TenantId</c> from
///   <see cref="Strg.Core.Domain.ITenantContext"/>. Without this pin, an audited command that
///   forgets the tenant marker would persist audit rows under <c>Guid.Empty</c> and look like a
///   global-tenant entry.</item>
///   <item>Every command handler that injects <see cref="IAuditScope"/> handles a command
///   marked <see cref="IAuditedCommand"/>. <see cref="IAuditScope"/> is dead weight without the
///   marker — <see cref="AuditBehavior{TMessage,TResponse}"/> only writes when the marker is
///   present, so a forgotten marker silently swallows the audit row.</item>
/// </list>
/// </summary>
public sealed class AuditedCommandConventionsTests
{
    [Fact]
    public void Every_IAuditedCommand_also_implements_ITenantScopedCommand()
    {
        var applicationAssembly = typeof(AuditBehavior<,>).Assembly;

        var auditedCommandTypes = applicationAssembly.GetTypes()
            .Where(t => !t.IsInterface && !t.IsAbstract)
            .Where(t => typeof(IAuditedCommand).IsAssignableFrom(t))
            .ToList();

        auditedCommandTypes.Should().NotBeEmpty(
            "Phase 2's CQRS migration adopted IAuditedCommand on Drives/Folders/Tags commands — " +
            "if this comes back empty the marker has been removed everywhere, not the convention");

        foreach (var commandType in auditedCommandTypes)
        {
            typeof(ITenantScopedCommand).IsAssignableFrom(commandType).Should().BeTrue(
                because: $"{commandType.FullName} is IAuditedCommand but not ITenantScopedCommand: " +
                         $"AuditScope.BuildEntry reads TenantId from ITenantContext, which is only " +
                         $"guaranteed non-empty when TenantScopeBehavior runs first. Without the " +
                         $"tenant marker, audit rows could land under Guid.Empty.");
        }
    }

    [Fact]
    public void Every_command_handler_injecting_IAuditScope_handles_an_IAuditedCommand()
    {
        var applicationAssembly = typeof(AuditBehavior<,>).Assembly;

        // Restrict to ICommandHandler implementations — pipeline behaviors (like AuditBehavior
        // itself) also inject IAuditScope, but they are the consumer of the scope, not the
        // populator, so they're outside this convention.
        var commandHandlersUsingScope = applicationAssembly.GetTypes()
            .Where(t => !t.IsInterface && !t.IsAbstract)
            .Where(t => ResolveCommandHandlerMessageType(t) is not null)
            .Where(InjectsAuditScope)
            .ToList();

        commandHandlersUsingScope.Should().NotBeEmpty(
            "Phase 2 migrated Drives/Folders/Tags handlers onto IAuditScope — if this comes back " +
            "empty the migration has been reverted, not the convention");

        foreach (var handler in commandHandlersUsingScope)
        {
            var commandType = ResolveCommandHandlerMessageType(handler)!;

            typeof(IAuditedCommand).IsAssignableFrom(commandType).Should().BeTrue(
                because: $"{commandType.FullName} is handled by {handler.FullName} (which injects " +
                         $"IAuditScope) but is not marked IAuditedCommand. AuditBehavior only writes " +
                         $"when the marker is present, so the audit row would be silently dropped.");
        }
    }

    private static bool InjectsAuditScope(Type handlerType)
    {
        return handlerType.GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Any(p => p.ParameterType == typeof(IAuditScope));
    }

    private static Type? ResolveCommandHandlerMessageType(Type handlerType)
    {
        foreach (var iface in handlerType.GetInterfaces())
        {
            if (!iface.IsGenericType)
            {
                continue;
            }

            var def = iface.GetGenericTypeDefinition();
            if (def == typeof(ICommandHandler<,>) || def == typeof(ICommandHandler<>))
            {
                return iface.GetGenericArguments()[0];
            }
        }

        return null;
    }
}
