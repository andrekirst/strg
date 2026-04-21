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
/// </summary>
public interface IQuotaService
{
    /// <summary>
    /// Advisory check: does the user currently have room for <paramref name="requiredBytes"/>?
    /// Does NOT reserve. A subsequent <see cref="CommitAsync"/> may still fail if another
    /// concurrent upload committed in between.
    /// </summary>
    Task<QuotaCheckResult> CheckAsync(Guid userId, long requiredBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically increments the user's used bytes by <paramref name="bytesAdded"/>, rejecting
    /// the change if the new total would exceed quota. Throws <see cref="QuotaExceededException"/>
    /// on shortfall so upload handlers can fail the request without branching on a Result.
    /// </summary>
    Task CommitAsync(Guid userId, long bytesAdded, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decrements used bytes (upload aborted, version superseded, file hard-deleted). Clamps at
    /// 0 so a bookkeeping mistake can't drive <c>UsedBytes</c> negative — quota accounting is
    /// eventually-consistent with storage, and an orphan-reaper correcting state later is less
    /// harmful than negative usage bypassing all subsequent quota checks.
    /// </summary>
    Task ReleaseAsync(Guid userId, long bytesReleased, CancellationToken cancellationToken = default);

    /// <summary>Current snapshot for quota display. Throws <c>NotFoundException</c> if the user
    /// does not exist in the caller's tenant.</summary>
    Task<QuotaInfo> GetInfoAsync(Guid userId, CancellationToken cancellationToken = default);
}

public sealed record QuotaCheckResult(bool IsAllowed, long Available, long Quota, long Used);

public sealed record QuotaInfo(long QuotaBytes, long UsedBytes, long FreeBytes, double UsagePercent);
