using Strg.Core.Domain;

namespace Strg.GraphQl.Tests.Helpers;

/// <summary>
/// Mutable <see cref="ICurrentUser"/> stub mirroring <c>TestTenantContext.Shared</c>. EF Core's
/// model cache captures the <c>ICurrentUser</c> reference (via the <c>Tag</c> user-scope query
/// filter) at first model compilation, so tests that need a stable closure target across the
/// shared schema must mutate the same instance rather than register a new one. Tests that need
/// to flip the current user mid-test (two-user isolation suites) overwrite
/// <see cref="UserId"/> directly on <see cref="Shared"/> between calls.
/// </summary>
internal sealed class TestCurrentUser : ICurrentUser
{
    public Guid UserId { get; set; }

    public static readonly TestCurrentUser Shared = new();
}
