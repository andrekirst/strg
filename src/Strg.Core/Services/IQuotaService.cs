using Strg.Core.Exceptions;

namespace Strg.Core.Services;

/// <summary>
/// Plaintext-denominated storage-quota accounting for a user. The encrypted-storage overhead
/// (envelope header + per-chunk tags, ~0.025% on 64 KiB chunks) is absorbed by the operator and
/// never charged to user quota — see STRG-026 #5.
///
/// <para><b>Check vs Commit.</b> <see cref="CheckAsync"/> is an advisory peek: two callers
/// racing can both see enough quota. <see cref="CommitAsync"/> is the single atomic gate —
/// exactly one of two 55 MiB commits against a 100 MiB quota survives. Treat Check as a
/// pre-flight UX signal, never a reservation.</para>
///
/// <para><b>ChangeTracker staleness (IMPORTANT).</b> Callers MUST NOT hold a tracked <c>User</c>
/// entity of the target user across a <see cref="IQuotaService"/> call. <c>ExecuteUpdateAsync</c>
/// bypasses EF Core's ChangeTracker — a subsequent <c>SaveChanges</c> on a tracked <c>User</c>
/// would overwrite the atomic UPDATE with a stale <c>UsedBytes</c> snapshot (lost-update). Either
/// re-query the user after the quota call or don't track it in the first place. We deliberately
/// don't call <c>Reload()</c> inside the service: upload volume makes that per-call reload
/// expensive, and the failure mode (stale overwrite) is tractable as a caller-side rule.</para>
///
/// <para><b>Missing-user surface.</b> A user that does not exist in the caller's tenant — a
/// typo'd userId, a cross-tenant probe, or a soft-deleted account — throws the same
/// <see cref="QuotaExceededException"/> *type* as a real quota shortfall on
/// <see cref="CheckAsync"/>, <see cref="CommitAsync"/>, and <see cref="GetInfoAsync"/>. This
/// uniform exception type means a caller who only inspects the type cannot probe existence.</para>
///
/// <para><b>Asymmetry caveat (load-bearing).</b> Only <see cref="CommitAsync"/> is fully symmetric
/// — it throws on both missing-user AND over-quota, so the throw-vs-success channel itself reveals
/// nothing. <see cref="CheckAsync"/> and <see cref="GetInfoAsync"/> return a
/// <see cref="QuotaCheckResult"/> / <see cref="QuotaInfo"/> on existing users (over quota or not)
/// and throw only on missing — so a caller passing an arbitrary userId can still distinguish
/// existence by throws-vs-returns. At v0.1 this is harmless because every Check/GetInfo call
/// site passes the JWT-sub userId (the caller's own), but any future admin/sharing/impersonation
/// path that accepts an arbitrary userId on these two methods would re-introduce the enumeration
/// oracle. Such paths MUST go through <see cref="IQuotaAdminService.TryCommitAsync"/>, whose
/// <see cref="CommitOutcome"/> return makes the distinction explicit and gates it behind
/// <c>AuthPolicies.Admin</c>.</para>
/// </summary>
public interface IQuotaService
{
    /// <summary>
    /// Advisory check: does the user currently have room for <paramref name="requiredBytes"/>?
    /// Does NOT reserve. A subsequent <see cref="CommitAsync"/> may still fail if another
    /// concurrent upload committed in between. Missing user collapses to
    /// <see cref="QuotaExceededException"/> per the class-level missing-user contract.
    /// </summary>
    Task<QuotaCheckResult> CheckAsync(Guid userId, long requiredBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically increments the user's used bytes by <paramref name="bytesAdded"/>, rejecting
    /// the change if the new total would exceed quota. Throws <see cref="QuotaExceededException"/>
    /// on shortfall so upload handlers can fail the request without branching on a Result.
    ///
    /// <para><b>CALLING ORDER (CRITICAL): Commit-first.</b> The correct upload sequence is
    /// <c>CommitAsync(n)</c> → write blob → <see cref="ReleaseAsync"/> on write-failure.
    /// Commit IS the reservation — the atomic <c>UPDATE ... WHERE used_bytes + n &lt;= quota</c>
    /// is the only race-safe budget gate. Never <c>Check → write → Commit</c>: another upload
    /// can drain the budget between Check and Commit, leaving you with a blob on disk that
    /// suddenly has no quota. On write-failure, call <c>ReleaseAsync(n)</c> to roll back.</para>
    /// </summary>
    Task CommitAsync(Guid userId, long bytesAdded, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decrements used bytes (upload aborted, version superseded, file hard-deleted). Clamps at
    /// 0 so a bookkeeping mistake can't drive <c>UsedBytes</c> negative — quota accounting is
    /// eventually-consistent with storage, and an orphan-reaper correcting state later is less
    /// harmful than negative usage bypassing all subsequent quota checks.
    ///
    /// <para>Silent no-op on missing user (rows-affected = 0); does NOT throw. A reaper that
    /// processes stale queues may hit soft-deleted or tenant-foreign users and must not panic.
    /// The implementation logs a warning in this case — typo'd userIds fail invisibly otherwise.</para>
    /// </summary>
    Task ReleaseAsync(Guid userId, long bytesReleased, CancellationToken cancellationToken = default);

    /// <summary>
    /// Current snapshot for quota display. Throws <see cref="QuotaExceededException"/> if the
    /// user does not exist in the caller's tenant (per the class-level missing-user contract).
    /// </summary>
    Task<QuotaInfo> GetInfoAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Administrative quota surface. Separate from <see cref="IQuotaService"/> so the
/// enumeration-oracle-unsafe outcomes never leak to untrusted callers. Bind this into DI only
/// for authenticated admin/diagnostic endpoints; regular upload handlers depend on
/// <see cref="IQuotaService"/> with its collapsed missing-user semantics.
/// </summary>
public interface IQuotaAdminService
{
    /// <summary>
    /// Non-throwing Commit that distinguishes <see cref="CommitOutcome.QuotaExceeded"/> from
    /// <see cref="CommitOutcome.UserNotInTenant"/>. Admin tools (usage reports, forced
    /// reservations, reaper-compensation dashboards) need the distinction — upload handlers
    /// don't, and getting it via <see cref="IQuotaService.CommitAsync"/> would force every
    /// caller to parse an enum just so the admin path could opt-in.
    /// </summary>
    Task<CommitOutcome> TryCommitAsync(Guid userId, long bytesAdded, CancellationToken cancellationToken = default);
}

public enum CommitOutcome
{
    Success,
    QuotaExceeded,
    UserNotInTenant,
}

public sealed record QuotaCheckResult(bool IsAllowed, long Available, long Quota, long Used);

public sealed record QuotaInfo(long QuotaBytes, long UsedBytes, long FreeBytes, long OverageBytes, double UsagePercent);
