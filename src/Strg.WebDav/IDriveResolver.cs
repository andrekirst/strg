using Strg.Core.Domain;

namespace Strg.WebDav;

/// <summary>
/// Resolves a URL-segment drive name to a <see cref="Drive"/> scoped to the caller's tenant.
///
/// <para>The <see cref="StrgWebDavMiddleware"/> calls this before dispatching any WebDAV verb so
/// an unknown drive short-circuits with 404 instead of reaching the per-verb handlers — the same
/// "fail-closed at the edge" posture <see cref="Core.Storage.StoragePath"/> applies to file paths.
/// </para>
///
/// <para><b>driveName comes from the URL and is therefore untrusted.</b> The implementation MUST
/// validate it against the <c>[a-z0-9-]</c> shape before the EF lookup — both as path-traversal
/// defence and as a cheap rejection of malformed requests before they touch the DB.</para>
/// </summary>
public interface IDriveResolver
{
    /// <summary>
    /// Returns the drive whose <see cref="Drive.Name"/> equals <paramref name="driveName"/> within
    /// the <paramref name="tenantId"/> scope, or <c>null</c> if no such drive exists, is
    /// soft-deleted, or the name fails the <c>[a-z0-9-]</c> validation.
    /// </summary>
    Task<Drive?> ResolveAsync(string driveName, Guid tenantId, CancellationToken cancellationToken = default);
}
