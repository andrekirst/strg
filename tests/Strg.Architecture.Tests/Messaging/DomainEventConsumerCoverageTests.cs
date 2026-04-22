using System.Reflection;
using FluentAssertions;
using MassTransit;
using Strg.Core.Domain;
using Strg.Core.Events;
using Xunit;

namespace Strg.Architecture.Tests.Messaging;

/// <summary>
/// Pins the orphaned-event guard from the STRG-065 audit INFO-2: every
/// <see cref="IDomainEvent"/> shipped from <c>Strg.Core.Events</c> must have at least one
/// <see cref="IConsumer{T}"/> registered somewhere in the Strg.* load graph — otherwise an
/// event is published into the outbox, dispatched by MassTransit, and quietly dropped with no
/// one listening.
///
/// <para><b>Why this test exists.</b> MassTransit is forgiving — publishing an event with no
/// consumer succeeds silently. A plausible regression shape: a developer adds a new domain
/// event, wires the publish side, and forgets to add the consumer. Everything compiles and
/// tests pass. The symptom only surfaces in prod when the "nothing happened" is noticed —
/// often weeks later, after the audit trail or search index is already missing entries.</para>
///
/// <para><b>Known orphans.</b> <see cref="BackupCompletedEvent"/> is the single documented
/// orphan: it was shipped early to stabilise the event type for the v0.2 backup subsystem,
/// but no consumer lands until the backup job itself does. The orphan allow-list is exact-match
/// so new orphans don't slip in silently — adding another event without a consumer fails this
/// test until the allow-list is updated deliberately (which forces a discussion about why
/// the event is shipping without a subscriber).</para>
/// </summary>
public sealed class DomainEventConsumerCoverageTests
{
    /// <summary>
    /// Events that intentionally have no consumer today. Entries here are a commitment to ship
    /// the consumer later — prefer not to grow this list. Each entry should name the tracker
    /// that will remove it.
    /// </summary>
    private static readonly IReadOnlySet<Type> KnownOrphans = new HashSet<Type>
    {
        // Tracker: v0.2 backup subsystem. The event type is shipped early so the publish side
        // of the backup job doesn't need a migration when the consumer lands.
        typeof(BackupCompletedEvent),
    };

    [Fact]
    public void Every_IDomainEvent_has_at_least_one_registered_consumer()
    {
        var eventTypes = typeof(IDomainEvent).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IDomainEvent).IsAssignableFrom(t))
            .ToList();

        eventTypes.Should().NotBeEmpty(
            "Strg.Core.Events should ship at least one IDomainEvent — if this fails, the " +
            "whole test is defeated");

        var consumedEventTypes = AssemblyLoader.StrgAssemblies
            .SelectMany(SafeGetTypes)
            .Where(t => t.IsClass && !t.IsAbstract)
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>))
            .Select(i => i.GetGenericArguments()[0])
            // Skip Fault<TEvent> consumers — those are dead-letter observers, not primary
            // subscribers, and an event can be "orphaned" even with a Fault consumer registered.
            .Where(t => !(t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Fault<>)))
            .ToHashSet();

        var orphans = eventTypes
            .Where(t => !consumedEventTypes.Contains(t))
            .Where(t => !KnownOrphans.Contains(t))
            .ToList();

        orphans.Should().BeEmpty(
            "every IDomainEvent must have a non-Fault IConsumer<T> registered in a Strg.* " +
            "assembly (or appear in KnownOrphans with a tracker citation). Orphaned events " +
            "are published into the outbox and silently dropped — the symptom only surfaces " +
            "when downstream consumers (audit, search-index, GraphQL subscriptions) are " +
            "noticed to be missing entries.");
    }

    [Fact]
    public void KnownOrphans_still_have_no_consumer()
    {
        // Defends against the opposite failure: a consumer lands for a previously-orphaned
        // event but KnownOrphans isn't updated. The list silently shields a regression the
        // moment someone adds a second orphan — this test fails when the allow-list is stale.
        var consumedEventTypes = AssemblyLoader.StrgAssemblies
            .SelectMany(SafeGetTypes)
            .Where(t => t.IsClass && !t.IsAbstract)
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>))
            .Select(i => i.GetGenericArguments()[0])
            .Where(t => !(t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Fault<>)))
            .ToHashSet();

        var staleOrphanEntries = KnownOrphans
            .Where(orphan => consumedEventTypes.Contains(orphan))
            .ToList();

        staleOrphanEntries.Should().BeEmpty(
            "KnownOrphans contains events that now have consumers — remove them from the " +
            "allow-list so future orphans can't hide behind a stale entry.");
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Partial type-load failures happen when a dependent assembly isn't resolvable —
            // fall back to the types that did load rather than failing the whole enumeration.
            // This is the narrow exception path; any other failure should surface.
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
