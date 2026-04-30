using Strg.Core.Domain;

namespace Strg.Integration.Tests.Common;

/// <summary>
/// Immutable <see cref="ICurrentUser"/> stub for integration tests that construct
/// <c>StrgDbContext</c> directly (outside the WebApplicationFactory DI). Mirrors the
/// <c>FixedTenantContext</c> pattern used in sibling fixtures. Tests that need to mutate
/// the current user mid-test should use the per-file <c>MutableCurrentUser</c> instead.
/// </summary>
internal sealed class FixedCurrentUser(Guid id) : ICurrentUser
{
    public Guid UserId => id;
}
