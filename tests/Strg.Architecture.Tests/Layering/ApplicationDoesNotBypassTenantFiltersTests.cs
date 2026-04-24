using FluentAssertions;
using Xunit;

namespace Strg.Architecture.Tests.Layering;

/// <summary>
/// Pins the CLAUDE.md tenant-isolation rule as applied to Strg.Application: no handler or
/// behavior in the orchestration layer may call <c>IgnoreQueryFilters</c>. The one carve-out
/// in CLAUDE.md is <c>UserRepository.GetByEmailAsync</c> in Strg.Infrastructure (a pre-auth
/// lookup that runs before any tenant is bound to the request). Every call site in
/// Strg.Application runs post-auth — so the carve-out does not apply.
///
/// <para>Source-text grep rather than reflection: <c>IgnoreQueryFilters</c> is an EF Core
/// extension method invocation, not a declared interface, so reflection cannot find it. The
/// text check catches the invocation regardless of how the call is written (method group,
/// fluent chain, etc.).</para>
/// </summary>
public sealed class ApplicationDoesNotBypassTenantFiltersTests
{
    [Fact]
    public void No_IgnoreQueryFilters_calls_in_Strg_Application_source()
    {
        var applicationSourceDir = Path.Combine(RepoPath.Root, "src", "Strg.Application");

        var offendingFiles = Directory
            .EnumerateFiles(applicationSourceDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("IgnoreQueryFilters", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepoPath.Root, path))
            .ToList();

        offendingFiles.Should().BeEmpty(
            because: "Strg.Application handlers run post-authentication, so the pre-auth carve-out " +
                     "documented in CLAUDE.md (UserRepository.GetByEmailAsync in Strg.Infrastructure) " +
                     "does not apply here. Tenant isolation is the primary security invariant of the " +
                     "platform — if any handler truly needs cross-tenant data, escalate it to a " +
                     "named admin service in Strg.Infrastructure with its own justification comment.");
    }
}
