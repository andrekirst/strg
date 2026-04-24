using FluentAssertions;
using Xunit;

namespace Strg.Architecture.Tests.Layering;

/// <summary>
/// Pins the CLAUDE.md tenant-isolation rule as applied to Strg.Application: no handler or
/// behavior in the orchestration layer may call <c>IgnoreQueryFilters</c>, with one narrow
/// exception enumerated in <see cref="LegitimateExceptions"/>. Every other hit fails the test.
///
/// <para>The carve-outs are not convenience — each one is a commented-on, reviewed-in invariant:
/// <list type="bullet">
///   <item><description>
///     <c>Features/Drives/Create/CreateDriveHandler.cs</c> — uniqueness check must span
///     soft-deleted rows so a deleted drive's name remains reserved within the tenant. The
///     handler re-applies the <c>TenantId</c> predicate inline because IgnoreQueryFilters
///     disables both the tenant filter and the soft-delete filter.
///   </description></item>
/// </list></para>
///
/// <para>Source-text grep rather than reflection: <c>IgnoreQueryFilters</c> is an EF Core
/// extension method invocation, not a declared interface, so reflection cannot find it. The
/// text check catches the invocation regardless of how the call is written (method group,
/// fluent chain, etc.).</para>
/// </summary>
public sealed class ApplicationDoesNotBypassTenantFiltersTests
{
    /// <summary>
    /// Files under <c>src/Strg.Application/</c> that are permitted to call
    /// <c>IgnoreQueryFilters</c>. Each entry must be justified inline in the handler with an
    /// <c>// ArchTest exception:</c> comment explaining why the bypass is load-bearing. Expanding
    /// this list is a reviewed, named decision — a default-deny list is the whole point.
    /// </summary>
    private static readonly string[] LegitimateExceptions =
    [
        Path.Combine("src", "Strg.Application", "Features", "Drives", "Create", "CreateDriveHandler.cs"),
    ];

    [Fact]
    public void IgnoreQueryFilters_calls_in_Strg_Application_match_the_allow_list()
    {
        var applicationSourceDir = Path.Combine(RepoPath.Root, "src", "Strg.Application");

        var exceptionPaths = LegitimateExceptions
            .Select(p => Path.Combine(RepoPath.Root, p))
            .ToHashSet(StringComparer.Ordinal);

        var offendingFiles = Directory
            .EnumerateFiles(applicationSourceDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("IgnoreQueryFilters", StringComparison.Ordinal))
            .Where(path => !exceptionPaths.Contains(path))
            .Select(path => Path.GetRelativePath(RepoPath.Root, path))
            .ToList();

        offendingFiles.Should().BeEmpty(
            because: "Strg.Application handlers run post-authentication, so the pre-auth carve-out " +
                     "documented in CLAUDE.md (UserRepository.GetByEmailAsync in Strg.Infrastructure) " +
                     "does not apply here. The only file currently allow-listed is CreateDriveHandler " +
                     "(drive-name uniqueness must span soft-deleted rows). Any new hit is a real bypass " +
                     "and must either be fixed or added to LegitimateExceptions with a justification.");

        // Mutation-check belt: if every allow-listed file stops calling IgnoreQueryFilters, the
        // allow-list entry is stale and should be removed. Surfacing this cheaply here so the
        // allow-list doesn't calcify into unused cruft.
        foreach (var exceptionPath in exceptionPaths)
        {
            if (!File.Exists(exceptionPath))
            {
                throw new InvalidOperationException(
                    $"LegitimateExceptions references non-existent file: {exceptionPath}. " +
                    "Either restore the file or remove the entry.");
            }

            File.ReadAllText(exceptionPath).Should().Contain(
                "IgnoreQueryFilters",
                $"{Path.GetRelativePath(RepoPath.Root, exceptionPath)} is allow-listed but no longer calls IgnoreQueryFilters — remove it from LegitimateExceptions.");
        }
    }
}
