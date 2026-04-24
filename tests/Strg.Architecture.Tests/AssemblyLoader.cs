using System.Reflection;
using MassTransit;
using Strg.Core.Domain;
using Strg.GraphQl.Consumers;
using Strg.Infrastructure.Data;
using Strg.WebDav;

namespace Strg.Architecture.Tests;

/// <summary>
/// Forces every <c>Strg.*</c> production assembly into the current AppDomain. Several ArchTests
/// walk <see cref="AppDomain.CurrentDomain"/>.<see cref="AppDomain.GetAssemblies"/> to answer
/// questions like "what consumers are registered?" or "is NWebDav.Server.AspNetCore in the
/// transitive graph?". Those questions only give the right answer if every production assembly
/// is loaded — which the runtime does lazily, and <c>typeof(T)</c> alone does <b>not</b>
/// guarantee the defining assembly is resolved (the JIT can emit a type-token reference without
/// triggering the full load). Reading <c>typeof(T).Assembly</c> and actively returning the
/// <see cref="Assembly"/> objects into a consumed list forces the resolve.
///
/// <para>The type references below are intentional and load-bearing; do not "clean up" the
/// discarded <c>.Assembly</c> reads — each one is what pins the declaring assembly into the
/// AppDomain before <see cref="AppDomain.GetAssemblies"/> is queried.</para>
/// </summary>
internal static class AssemblyLoader
{
    private static readonly Lazy<IReadOnlyList<Assembly>> _strgAssemblies = new(LoadStrgAssemblies);

    public static IReadOnlyList<Assembly> StrgAssemblies => _strgAssemblies.Value;

    private static IReadOnlyList<Assembly> LoadStrgAssemblies()
    {
        // Force-load each Strg.* production assembly by grabbing the declaring Assembly of a
        // canonical type and putting it into a list the CLR can't elide. A bare `typeof(X)` is
        // not enough — the JIT can resolve the type token without loading the full assembly,
        // which then hides the assembly's references from reflection walks.
        var seeds = new[]
        {
            typeof(TenantedEntity).Assembly,              // Strg.Core
            typeof(StrgDbContext).Assembly,               // Strg.Infrastructure
            typeof(GraphQlSubscriptionPublisher).Assembly, // Strg.GraphQl
            typeof(IStrgWebDavStore).Assembly,            // Strg.WebDav
            typeof(IConsumer<>).Assembly,                 // MassTransit (for IConsumer<T>)
        };

        // Touch each seed so the JIT can't elide the typeof reads.
        foreach (var seed in seeds)
        {
            _ = seed.FullName;
        }

        // Strg.Api is intentionally not referenced by this project (see csproj comment).
        // No consumer lives there anyway — the GraphQL bridge consumer lands via a
        // Program.cs callback and the consumer type itself lives in Strg.GraphQl.

        return AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Strg.", StringComparison.Ordinal) == true)
            .ToList();
    }
}
