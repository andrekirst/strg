using Microsoft.EntityFrameworkCore;
using Strg.Core.Exceptions;
using Strg.Core.Services;
using Strg.Infrastructure.Data;

namespace Strg.Infrastructure.Services;

/// <summary>
/// <see cref="IQuotaService"/> backed by <see cref="StrgDbContext"/>. All mutating operations
/// compile to a single atomic SQL UPDATE via <see cref="RelationalQueryableExtensions.ExecuteUpdateAsync{TSource}"/>
/// — the WHERE clause enforces the pre-condition, the SET clause performs the mutation, and the
/// rows-affected return value is the success signal. Row count of 0 means the pre-condition
/// failed (quota exceeded for <see cref="CommitAsync"/>, user missing for the others).
///
/// <para><b>Why not ChangeTracker?</b> A tracked-entity update (<c>user.UsedBytes += n; SaveChangesAsync()</c>)
/// requires a read-then-write cycle. Two concurrent uploads both reading <c>UsedBytes = 90</c>,
/// both writing <c>95</c>, would together consume 10 bytes of budget while charging only 5 —
/// the classic lost-update anomaly. The SQL UPDATE's <c>WHERE ... AND used_bytes + @delta &lt;= quota_bytes</c>
/// is evaluated inside the same transaction that performs the write, so Postgres' row-lock
/// serialises the check-and-set.</para>
///
/// <para><b>Tenant isolation.</b> Global query filters on <see cref="StrgDbContext"/> extend to
/// <c>ExecuteUpdateAsync</c>, so a caller in tenant A cannot commit or release against tenant
/// B's user — the WHERE gains an implicit <c>tenant_id = @currentTenant</c> clause.</para>
/// </summary>
public sealed class QuotaService(StrgDbContext db) : IQuotaService
{
    public async Task<QuotaCheckResult> CheckAsync(Guid userId, long requiredBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);

        var snapshot = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.QuotaBytes, u.UsedBytes })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            throw new NotFoundException($"User '{userId}' not found.");
        }

        var available = Math.Max(0, snapshot.QuotaBytes - snapshot.UsedBytes);
        var isAllowed = snapshot.UsedBytes + requiredBytes <= snapshot.QuotaBytes;
        return new QuotaCheckResult(isAllowed, available, snapshot.QuotaBytes, snapshot.UsedBytes);
    }

    public async Task CommitAsync(Guid userId, long bytesAdded, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesAdded);

        if (bytesAdded == 0)
        {
            // Skip the round-trip — the atomic UPDATE would be a no-op but still hit the DB.
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var rowsAffected = await db.Users
            .Where(u => u.Id == userId && u.UsedBytes + bytesAdded <= u.QuotaBytes)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(u => u.UsedBytes, u => u.UsedBytes + bytesAdded)
                    .SetProperty(u => u.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        if (rowsAffected == 0)
        {
            // Either the user doesn't exist in this tenant or the pre-condition failed. Both
            // collapse into "quota exceeded" from the caller's perspective — the upload path
            // can't disambiguate and shouldn't need to (tenant mismatch at this layer is a bug,
            // not a user-facing error). Audit logs at the endpoint layer still carry the userId.
            throw new QuotaExceededException();
        }
    }

    public async Task ReleaseAsync(Guid userId, long bytesReleased, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesReleased);

        if (bytesReleased == 0)
        {
            return;
        }

        // Clamp at 0: a caller releasing more than was committed (double-release, stale size
        // info) must not drive UsedBytes negative. Negative usage would silently grant unlimited
        // quota on subsequent Commits. SQL-side CASE is atomic with the update.
        var now = DateTimeOffset.UtcNow;
        await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(u => u.UsedBytes, u => u.UsedBytes < bytesReleased ? 0 : u.UsedBytes - bytesReleased)
                    .SetProperty(u => u.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<QuotaInfo> GetInfoAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var snapshot = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.QuotaBytes, u.UsedBytes })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            throw new NotFoundException($"User '{userId}' not found.");
        }

        var quota = snapshot.QuotaBytes;
        var used = snapshot.UsedBytes;
        var free = Math.Max(0, quota - used);
        // Clamp to [0, 100]: bookkeeping drift can briefly push used past quota (parallel commit
        // slipping past a stale Check, or a quota reduction by admin while uploads in flight).
        // A UsagePercent > 100 in the API surface confuses dashboards more than it helps.
        var usagePercent = quota == 0 ? 0d : Math.Min(100d, Math.Max(0d, (double)used / quota * 100d));
        return new QuotaInfo(quota, used, free, usagePercent);
    }
}
