using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.WebDav;

/// <summary>
/// EF-backed <see cref="IDriveResolver"/>. Sits on the regular <see cref="StrgDbContext"/> so the
/// multi-tenant global query filter transparently scopes the lookup — no
/// <c>IgnoreQueryFilters</c> here, because the resolver runs AFTER the authentication middleware
/// populated <see cref="Infrastructure.Data.ITenantContext"/> from the JWT.
///
/// <para><b>Path-traversal defence.</b> <c>driveName</c> arrives straight from the URL segment.
/// The <see cref="ValidDriveName"/> pattern is the twin of
/// <c>Strg.GraphQl.Mutations.Storage.DriveMutations.ValidDriveName</c> — same shape as the
/// write-side guard, so a name that can't be created via GraphQL can't be impersonated via the
/// WebDAV URL either. A <c>/</c>, <c>..</c>, or NUL byte trips the regex and the resolver returns
/// <c>null</c> before the DB is touched.</para>
/// </summary>
internal sealed partial class DriveResolver(StrgDbContext db) : IDriveResolver
{
    private static readonly Regex ValidDriveName = CompiledValidDriveName();

    public async Task<Drive?> ResolveAsync(string driveName, Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(driveName) || !ValidDriveName.IsMatch(driveName))
        {
            return null;
        }

        // Per STRG-067 acceptance criterion "Drive name case-insensitive lookup": the regex already
        // forces lower-case input, but PostgreSQL comparisons are case-sensitive by default, so the
        // explicit lower-casing here keeps the contract future-proof against any relaxation of the
        // write-side regex that would admit mixed-case stored names.
        var normalized = driveName.ToLowerInvariant();

        return await db.Drives
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.Name == normalized, cancellationToken);
    }

    public async Task<Guid?> GetDriveTenantIdAsync(string driveName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(driveName) || !ValidDriveName.IsMatch(driveName))
        {
            return null;
        }

        var normalized = driveName.ToLowerInvariant();

        // STRG-073 fold-in #3 — CLAUDE.md pre-auth carve-out applies here. The bridge calls this
        // BEFORE the branch's UseAuthentication has validated the JWT and populated ITenantContext,
        // so a tenant-filtered query would resolve TenantId to Guid.Empty and always return null,
        // defeating the cross-tenant verification the bridge needs. IgnoreQueryFilters drops the
        // tenant scope while the explicit `DeletedAt == null` predicate keeps soft-delete
        // enforcement intact — a soft-deleted drive must not surface its tenant via this path.
        //
        // Predicate uses `!d.DeletedAt.HasValue` (the mapped column) rather than `!d.IsDeleted`
        // (a computed CLR property that DriveConfiguration explicitly .Ignore()s). Mirroring the
        // global soft-delete filter's shape in StrgDbContext keeps the two in lockstep.
        //
        // Returning only the TenantId (not the Drive row) keeps the bridge's surface minimal: it
        // has no business with drive metadata, only with the tenant-match comparison.
        return await db.Drives
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(d => d.Name == normalized && !d.DeletedAt.HasValue)
            .Select(d => (Guid?)d.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CompiledValidDriveName();
}
