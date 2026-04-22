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

    /// <summary>
    /// STRG-073 fold-in #3 — returns the <see cref="Drive.TenantId"/> for the drive whose
    /// <see cref="Drive.Name"/> matches <paramref name="driveName"/> <b>across all tenants</b>,
    /// or <c>null</c> if no drive by that name exists anywhere (or the name fails validation,
    /// or the drive is soft-deleted). Sole consumer is
    /// <see cref="BasicAuthJwtBridgeMiddleware"/>'s cross-tenant verification step, which runs
    /// before the branch's <c>UseAuthentication</c> populates <c>ITenantContext</c> — a
    /// tenant-scoped lookup would always return <c>null</c> at that point in the pipeline.
    ///
    /// <para><b>Why this method exists on top of <see cref="ResolveAsync"/>.</b> The attack the
    /// bridge defends against is a cross-tenant credential oracle: without this check, a 404 on
    /// mismatched-tenant drives vs a 401 on wrong passwords would let an attacker probe whether
    /// any tenant's alice has a given password (valid creds → tenant-mismatched drive → 404;
    /// invalid creds → 401). The bridge collapses both cases to 401 by verifying the JWT's
    /// tenant claim equals the drive's tenant; this requires looking up the drive's tenant
    /// independently of the caller's claimed tenant.</para>
    ///
    /// <para><b>CLAUDE.md carve-out.</b> This is a pre-auth resolver path (JWT just issued, not
    /// yet validated by <c>UseAuthentication</c>). The implementation MAY use
    /// <c>IgnoreQueryFilters()</c> to bypass the tenant filter, but MUST keep soft-delete
    /// (<c>IsDeleted = false</c>) enforcement inline so a deleted drive cannot leak its tenant
    /// via this surface.</para>
    /// </summary>
    Task<Guid?> GetDriveTenantIdAsync(string driveName, CancellationToken cancellationToken = default);
}
