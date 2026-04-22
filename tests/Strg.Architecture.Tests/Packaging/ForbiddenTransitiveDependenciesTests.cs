using FluentAssertions;
using Xunit;

namespace Strg.Architecture.Tests.Packaging;

/// <summary>
/// Pins the STRG-068 audit INFO-1 invariant: the CVE-vulnerable NWebDav adapter must never
/// appear in the transitive NuGet graph. The original xmldoc rationale lives on
/// <c>IStrgWebDavStore</c> — GHSA-hxrm-9w7p-39cc is specific to
/// <c>NWebDav.Server.AspNetCore</c>, and STRG-068 deliberately takes only
/// <c>NWebDav.Server</c> (the non-vulnerable core) as a direct dep.
///
/// <para><b>Why hardcode the forbidden name.</b> The standard approach would be an allow-list
/// of packages we *do* want, with everything else rejected — but that's impractical for a
/// solution that pulls in dozens of Microsoft.* + MassTransit.* transitive packages. The
/// inverse — hardcoding the single known-bad package name — is cheap, self-documenting, and
/// defends against the specific regression shape: a new WebDAV-adjacent PR that adds
/// <c>NWebDav.Server.AspNetCore</c> transitively via some convenience wrapper.</para>
///
/// <para>The check walks <c>AppDomain.CurrentDomain.GetAssemblies()</c> rather than the NuGet
/// lock file so it catches both direct references and transitive loads. The
/// <see cref="AssemblyLoader"/> hook forces every <c>Strg.*</c> assembly into the domain so
/// their downstream references are observable.</para>
/// </summary>
public sealed class ForbiddenTransitiveDependenciesTests
{
    private const string ForbiddenNWebDavAdapter = "NWebDav.Server.AspNetCore";

    [Fact]
    public void NWebDav_Server_AspNetCore_is_not_in_the_loaded_or_referenced_assembly_graph()
    {
        // Force every Strg.* assembly to load so its direct references are enumerable.
        _ = AssemblyLoader.StrgAssemblies;

        var loadedAssemblyNames = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name)
            .ToList();

        var referencedAssemblyNames = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => a.GetReferencedAssemblies())
            .Select(a => a.Name)
            .ToList();

        var allNames = loadedAssemblyNames.Concat(referencedAssemblyNames)
            .Where(n => n is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        allNames.Should().NotContain(
            ForbiddenNWebDavAdapter,
            because: "NWebDav.Server.AspNetCore carries GHSA-hxrm-9w7p-39cc. STRG-068 takes " +
                     "NWebDav.Server (the non-vulnerable core) directly and exposes its own " +
                     "IStrgWebDavStore bridge — see xmldoc on IStrgWebDavStore. A transitive " +
                     "add would re-introduce the vulnerability silently.");
    }

    [Fact]
    public void Microsoft_AspNetCore_Http_Abstractions_is_at_or_above_the_net10_baseline()
    {
        // Floor is .NET 10's shared-framework version (10.0). The dispatch asks for a CVE
        // range but the named GHSA lives on NWebDav.Server.AspNetCore — for the ASP.NET Core
        // side the robust invariant is "don't silently downgrade below the TFM-bundled
        // version", which a rogue package pin on an older ASP.NET major would do. A tighter
        // floor can replace this if a specific Microsoft.AspNetCore advisory lands.
        //
        // Target: Microsoft.AspNetCore.Http.Abstractions. Why not the concrete
        // Microsoft.AspNetCore.Http? The concrete assembly only loads when the host project
        // (Strg.Api) is referenced; this Architecture.Tests project deliberately omits that
        // reference (see csproj comment) so the test surface stays buildable even when in-flight
        // work on Strg.Api has transient compile errors. Abstractions IS in the graph — both
        // Strg.Infrastructure and Strg.GraphQL take it via FrameworkReference
        // Microsoft.AspNetCore.App, and it ships in lockstep with Microsoft.AspNetCore.Http from
        // the same shared framework, so a rogue downgrade pin would move both together.
        _ = AssemblyLoader.StrgAssemblies;

        var aspNetHttp = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => a.GetReferencedAssemblies())
            .Concat(AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName()))
            .FirstOrDefault(n => n.Name == "Microsoft.AspNetCore.Http.Abstractions");

        aspNetHttp.Should().NotBeNull(
            "Microsoft.AspNetCore.Http.Abstractions ships as part of the Microsoft.AspNetCore.App " +
            "shared framework reference — it must appear in the graph, otherwise neither the " +
            "tenant context middleware nor the WebDAV + GraphQL hosts are really ASP.NET Core apps");

        aspNetHttp!.Version!.Major.Should().BeGreaterThanOrEqualTo(
            10,
            because: "the TFM-bundled Microsoft.AspNetCore.Http.Abstractions version for net10.0 " +
                     "is 10.x. A rogue pin on an older major (e.g. net6/7/8 range) would bypass " +
                     ".NET 10 shared-framework patches and re-expose any ASP.NET Core CVEs " +
                     "patched in the 9.x/10.x windows — the concrete Microsoft.AspNetCore.Http " +
                     "moves in lockstep with Abstractions, so a version-floor here defends both.");
    }
}
