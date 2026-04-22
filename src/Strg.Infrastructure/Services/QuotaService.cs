using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Strg.Core.Domain;
using Strg.Core.Events;
using Strg.Core.Exceptions;
using Strg.Core.Services;
using Strg.Infrastructure.Data;

namespace Strg.Infrastructure.Services;

/// <summary>
/// <see cref="IQuotaService"/> (and <see cref="IQuotaAdminService"/>) backed by
/// <see cref="StrgDbContext"/>. All mutating operations compile to a single atomic SQL UPDATE via
/// <see cref="RelationalQueryableExtensions.ExecuteUpdateAsync{TSource}"/> — the WHERE clause
/// enforces the pre-condition, the SET clause performs the mutation, and the rows-affected
/// return value is the success signal. Row count of 0 means the pre-condition failed (quota
/// exceeded for <see cref="CommitAsync"/>, user missing for the others).
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
public sealed class QuotaService(
    StrgDbContext db,
    ITenantContext tenantContext,
    IPublishEndpoint publishEndpoint,
    ILogger<QuotaService> logger) : IQuotaService, IQuotaAdminService
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
            // Missing user collapses to QuotaExceededException per the class-level contract on
            // IQuotaService — returning a distinguishable result would turn CheckAsync into an
            // enumeration oracle across tenants.
            throw new QuotaExceededException();
        }

        var available = Math.Max(0, snapshot.QuotaBytes - snapshot.UsedBytes);
        // Subtractive form avoids additive overflow on pathological requiredBytes values. C# is
        // unchecked by default; `UsedBytes + long.MaxValue` wraps to negative and would silently
        // return IsAllowed = true. `requiredBytes <= QuotaBytes - UsedBytes` cannot wrap because
        // both operands of the subtraction are non-negative and the result is already bounded
        // by QuotaBytes.
        var isAllowed = requiredBytes <= snapshot.QuotaBytes - snapshot.UsedBytes;
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

        // Atomicity goal: UPDATE + threshold-cross detection + outbox Publish + SaveChangesAsync
        // either all land or none. Without that, an outbox row could persist while the UPDATE
        // rolls back (dual-write), producing a QuotaWarningEvent for a commit that never
        // happened — and the reverse would publish a commit without the warning. If the caller
        // already holds a transaction (FileVersionStore.CreateVersionAsync wraps version insert
        // + quota commit + SaveChanges in one), we participate in theirs; only open our own
        // when there is no ambient transaction to enlist in.
        var now = DateTimeOffset.UtcNow;
        var ownTransaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
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
                // not a user-facing error). Admin paths use IQuotaAdminService.TryCommitAsync.
                throw new QuotaExceededException();
            }

            await PublishThresholdWarningIfCrossedAsync(userId, bytesAdded, cancellationToken)
                .ConfigureAwait(false);

            if (ownTransaction is not null)
            {
                await ownTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (ownTransaction is not null)
            {
                await ownTransaction.DisposeAsync().ConfigureAwait(false);
            }
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
        var rowsAffected = await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(u => u.UsedBytes, u => u.UsedBytes < bytesReleased ? 0 : u.UsedBytes - bytesReleased)
                    .SetProperty(u => u.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        if (rowsAffected == 0)
        {
            // Legitimate: a reaper processing a stale queue may hit soft-deleted or
            // tenant-foreign users. NOT an error — see IQuotaService.ReleaseAsync contract. Log
            // a warning so a typo'd userId in operator tooling doesn't fail invisibly.
            logger.LogWarning(
                "QuotaService.ReleaseAsync: user {UserId} not found in tenant {TenantId}; {Bytes} released bytes ignored",
                userId,
                tenantContext.TenantId,
                bytesReleased);
        }
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
            // Collapse to QuotaExceededException per the class-level missing-user contract.
            throw new QuotaExceededException();
        }

        var quota = snapshot.QuotaBytes;
        var used = snapshot.UsedBytes;
        var free = Math.Max(0, quota - used);
        // OverageBytes is the raw (unclamped) amount by which Used exceeds Quota. Lets dashboards
        // paint a red "over by N" indicator without doing the math themselves; UsagePercent
        // stays clamped to [0, 100] for pie-chart display sanity.
        var overage = Math.Max(0, used - quota);
        // Clamp to [0, 100]: bookkeeping drift can briefly push used past quota (parallel commit
        // slipping past a stale Check, or a quota reduction by admin while uploads in flight).
        // A UsagePercent > 100 in the API surface confuses dashboards more than it helps.
        var usagePercent = quota == 0 ? 0d : Math.Min(100d, Math.Max(0d, (double)used / quota * 100d));
        return new QuotaInfo(quota, used, free, overage, usagePercent);
    }

    // ── IQuotaAdminService ─────────────────────────────────────────────────────
    //
    // Only the admin surface distinguishes "user not in tenant" from "quota exceeded". Wire
    // IQuotaAdminService into DI only for authenticated admin endpoints.

    public async Task<CommitOutcome> TryCommitAsync(Guid userId, long bytesAdded, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesAdded);

        if (bytesAdded == 0)
        {
            // Probing for existence with 0 bytes would leak the enumeration oracle anyway, but
            // the no-op is still cheapest: if the user exists, Success; we issue a single
            // existence probe to disambiguate.
            var exists = await db.Users.AnyAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
            return exists ? CommitOutcome.Success : CommitOutcome.UserNotInTenant;
        }

        // See CommitAsync: participate in an ambient transaction if one exists (admin callers may
        // chain TryCommitAsync inside a larger workflow). Opening a nested BeginTransactionAsync
        // on the same connection throws InvalidOperationException on PostgreSQL.
        var now = DateTimeOffset.UtcNow;
        var ownTransaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            var rowsAffected = await db.Users
                .Where(u => u.Id == userId && u.UsedBytes + bytesAdded <= u.QuotaBytes)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(u => u.UsedBytes, u => u.UsedBytes + bytesAdded)
                        .SetProperty(u => u.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);

            if (rowsAffected > 0)
            {
                await PublishThresholdWarningIfCrossedAsync(userId, bytesAdded, cancellationToken)
                    .ConfigureAwait(false);
                if (ownTransaction is not null)
                {
                    await ownTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                return CommitOutcome.Success;
            }

            // rows-affected = 0: either the user doesn't exist (tenant filter + Id mismatch) OR
            // the quota pre-condition failed. The main IQuotaService collapses both; here we
            // issue a single follow-up existence probe to disambiguate. Cheap compared to the
            // admin value it unlocks (distinguishing typo'd userId from real over-quota in
            // diagnostic tooling). Only roll back our own transaction — inside an ambient one,
            // rollback is the caller's call since the UPDATE matched 0 rows and is a no-op.
            if (ownTransaction is not null)
            {
                await ownTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            var userExists = await db.Users.AnyAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
            return userExists ? CommitOutcome.QuotaExceeded : CommitOutcome.UserNotInTenant;
        }
        finally
        {
            if (ownTransaction is not null)
            {
                await ownTransaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Reads the post-UPDATE usage snapshot and publishes a <see cref="QuotaWarningEvent"/>
    /// when the commit crosses the <see cref="QuotaThresholds.Warning"/> or
    /// <see cref="QuotaThresholds.Critical"/> ratio. The "before" ratio is derived as
    /// <c>(post.UsedBytes - bytesAdded) / post.QuotaBytes</c> — safe because the SELECT runs
    /// inside the same transaction that holds the row lock from the UPDATE, so no concurrent
    /// write has slipped in between.
    /// </summary>
    /// <remarks>
    /// Crossing semantics: a commit from 79% → 81% publishes; a subsequent 81% → 82% commit does
    /// NOT. Prevents notification spam inside the warning band. The consumer
    /// (<see cref="Messaging.Consumers.QuotaNotificationConsumer"/>) carries EventId
    /// idempotency, so at-least-once redelivery still collapses to a single Notification row.
    /// </remarks>
    private async Task PublishThresholdWarningIfCrossedAsync(
        Guid userId, long bytesAdded, CancellationToken cancellationToken)
    {
        // Row is locked by the in-transaction UPDATE; this SELECT returns the just-committed
        // values without race. Tenant filter still applies — if the UPDATE succeeded, the user
        // is in the current tenant, so the SELECT returns a row.
        var snapshot = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.TenantId, u.UsedBytes, u.QuotaBytes })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null || snapshot.QuotaBytes <= 0)
        {
            // QuotaBytes == 0 means unlimited/unmetered — threshold ratios are undefined.
            return;
        }

        var afterRatio = (double)snapshot.UsedBytes / snapshot.QuotaBytes;
        var beforeRatio = (double)(snapshot.UsedBytes - bytesAdded) / snapshot.QuotaBytes;

        if (!CrossedThreshold(beforeRatio, afterRatio))
        {
            return;
        }

        await publishEndpoint.Publish(
            new QuotaWarningEvent(snapshot.TenantId, userId, snapshot.UsedBytes, snapshot.QuotaBytes),
            cancellationToken).ConfigureAwait(false);

        // SaveChangesAsync flushes the MassTransit outbox row within our transaction. Without
        // this, the Publish would remain in the DbContext's pending-outbox list and be lost
        // when the DbContext is disposed without a later SaveChangesAsync.
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool CrossedThreshold(double before, double after) =>
        (before < QuotaThresholds.Warning && after >= QuotaThresholds.Warning)
        || (before < QuotaThresholds.Critical && after >= QuotaThresholds.Critical);
}
