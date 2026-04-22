using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Data.Configurations;

namespace Strg.WebDav;

/// <summary>
/// STRG-072 — database-backed <see cref="IStrgWebDavLockManager"/> over the <c>FileLocks</c> table.
///
/// <para><b>Why not in-memory?</b> A singleton dictionary looks tempting ("locks are ephemeral
/// anyway"), but three failure modes ruled it out:
/// <list type="number">
///   <item><description><b>Multi-replica</b>: two API pods behind a load balancer would each hold
///     their own view of the lock table. Client A locks on pod 1, client B locks on pod 2 → both
///     get ACQUIRED, both write, last-writer-wins corruption.</description></item>
///   <item><description><b>Process recycle</b>: Kubernetes pod churn, blue/green, app-pool recycle —
///     any of these would silently drop every active lock. A client holding an hour-long lock
///     would suddenly find another client free to overwrite its file mid-edit.</description></item>
///   <item><description><b>Tenant isolation</b>: with EF's global query filters we get tenant +
///     soft-delete scoping for free; reimplementing that in a dictionary is a correctness
///     liability.</description></item>
/// </list>
/// </para>
///
/// <para><b>Atomic insert.</b> The <see cref="LockAsync"/> path does NOT do query-then-insert.
/// It goes straight to INSERT and relies on the FULL unique index <c>(TenantId, ResourceUri)</c>
/// to raise 23505 when another lock already occupies the slot. Catching
/// <see cref="DbUpdateException"/> on that specific constraint name is the single-source-of-truth
/// for "another lock won the race". A query-first approach would have a TOCTOU window between
/// the SELECT and the INSERT that two simultaneous LOCKs could both pass.</para>
///
/// <para><b>Stale-row sweep.</b> The unique index is unfiltered (Postgres rejects
/// <c>WHERE ExpiresAt &gt; NOW()</c> with 42P17 because NOW() is STABLE, not IMMUTABLE — see
/// <see cref="Strg.Infrastructure.Data.Configurations.FileLockConfiguration"/>), so an expired
/// row on the same resource would block a fresh LOCK just as surely as an active one would.
/// <see cref="LockAsync"/> runs a DELETE of expired rows on <c>(TenantId, ResourceUri)</c>
/// BEFORE the INSERT inside a single transaction. Live races are still serialized by the unique
/// index: if two concurrent LOCKs both delete-nothing (no expired row) and both INSERT, one
/// commits and the other gets 23505 → <see cref="LockResult.Conflict"/>, exactly as before.</para>
///
/// <para><b>Token entropy.</b> <see cref="GenerateSecureToken"/> uses
/// <c>RandomNumberGenerator.GetBytes(16)</c> — 128 bits of CSPRNG output, not
/// <see cref="Guid.NewGuid"/>. Lock tokens are capability tokens: whoever presents one can unlock
/// the resource or reuse the lock for PUT. <see cref="Guid.NewGuid"/> on .NET today is sourced
/// from a CSPRNG in practice, but that's not the documented contract — a future switch to a
/// faster non-crypto generator (see the v7 time-ordered discussion) would silently weaken the
/// surface without any public-API change. Pinning <see cref="RandomNumberGenerator"/> makes the
/// guarantee load-bearing rather than incidental.</para>
/// </summary>
public sealed class DbLockManager(
    StrgDbContext db,
    ITenantContext tenantContext,
    ILogger<DbLockManager> logger) : IStrgWebDavLockManager
{
    public async Task<LockResult> LockAsync(
        string resourceUri,
        Guid ownerId,
        string? ownerInfo,
        TimeSpan timeout,
        Func<string>? tokenFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceUri);

        var token = (tokenFactory ?? GenerateSecureToken)();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(timeout);
        var tenantId = tenantContext.TenantId;

        var fileLock = new FileLock
        {
            TenantId = tenantId,
            ResourceUri = resourceUri,
            Token = token,
            OwnerId = ownerId,
            OwnerInfo = ownerInfo,
            ExpiresAt = expiresAt,
        };

        // Sweep any expired row on the same (TenantId, ResourceUri) inside a transaction so the
        // DELETE and INSERT land atomically. See class xmldoc for why Postgres rejects the
        // partial-index alternative. The DELETE's WHERE is tenant-scoped explicitly — we're not
        // relying on global query filters for ExecuteDeleteAsync paths, on principle.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.FileLocks
            .Where(l => l.TenantId == tenantId
                && l.ResourceUri == resourceUri
                && l.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken);

        db.FileLocks.Add(fileLock);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            logger.LogDebug(
                "WebDAV: acquired lock on {ResourceUri} for owner {OwnerId} (expires {ExpiresAt})",
                resourceUri, ownerId, expiresAt);
            return LockResult.Acquired(fileLock);
        }
        catch (DbUpdateException ex) when (IsActiveLockConflict(ex))
        {
            // Unique index fired — another lock occupies (TenantId, ResourceUri). It must be
            // active, because we just DELETEd any expired rows at this key in the same
            // transaction, and a competing INSERT that raced us into the unique constraint can
            // only have been an active-lock write. Detach the tracker state and roll back so the
            // DbContext stays clean for subsequent work.
            db.Entry(fileLock).State = EntityState.Detached;
            await tx.RollbackAsync(cancellationToken);

            var existing = await GetLockAsync(resourceUri, cancellationToken);
            if (existing is null)
            {
                // Competing lock must have expired or been released between the 23505 and this
                // re-read. Surface Conflict anyway (not Acquired) so the client sees a
                // deterministic "try again" shape rather than a ghost success.
                logger.LogInformation(
                    "WebDAV: lock conflict on {ResourceUri} resolved to no-active-lock on re-read — race between release and our insert",
                    resourceUri);
            }
            return LockResult.Conflict(existing!);
        }
    }

    public async Task<bool> UnlockAsync(string resourceUri, string token, Guid ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceUri);
        ArgumentException.ThrowIfNullOrEmpty(token);

        var now = DateTimeOffset.UtcNow;

        // ExecuteDelete is the race-free path: no load-then-delete TOCTOU, no ChangeTracker state
        // to reset. The WHERE clause IS the authorization check — token + owner + still-active —
        // so we never delete a lock we shouldn't, even if the row changed between a hypothetical
        // SELECT and the DELETE.
        var deleted = await db.FileLocks
            .Where(l => l.ResourceUri == resourceUri
                && l.Token == token
                && l.OwnerId == ownerId
                && l.ExpiresAt > now)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted == 0)
        {
            logger.LogDebug(
                "WebDAV: UNLOCK rejected on {ResourceUri} — no matching active lock (wrong token, wrong owner, or already expired)",
                resourceUri);
            return false;
        }
        return true;
    }

    public async Task<FileLock?> RefreshAsync(string resourceUri, string token, Guid ownerId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceUri);
        ArgumentException.ThrowIfNullOrEmpty(token);

        var now = DateTimeOffset.UtcNow;
        var newExpiresAt = now.Add(timeout);

        // Load-then-save so we return the updated row. Refresh is infrequent (only on explicit
        // client renewal) and the uniqueness scope already guarantees at most one active lock
        // per resource, so we don't need the ExecuteUpdate fast-path here — returning the
        // refreshed entity matters for PROPFIND lockdiscovery callers.
        var fileLock = await db.FileLocks
            .FirstOrDefaultAsync(
                l => l.ResourceUri == resourceUri
                    && l.Token == token
                    && l.OwnerId == ownerId
                    && l.ExpiresAt > now,
                cancellationToken);

        if (fileLock is null)
        {
            return null;
        }

        fileLock.ExpiresAt = newExpiresAt;
        await db.SaveChangesAsync(cancellationToken);
        return fileLock;
    }

    public async Task<FileLock?> GetLockAsync(string resourceUri, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.FileLocks
            .AsNoTracking()
            .FirstOrDefaultAsync(
                l => l.ResourceUri == resourceUri && l.ExpiresAt > now,
                cancellationToken);
    }

    public async Task<bool> CanWriteAsync(string resourceUri, Guid ownerId, string? ifToken, CancellationToken cancellationToken = default)
    {
        var active = await GetLockAsync(resourceUri, cancellationToken);
        if (active is null)
        {
            // No active lock → write is unconditionally allowed. The whole point of class-2 locks
            // is opt-in serialization; absence of a lock doesn't imply any restriction.
            return true;
        }

        if (string.IsNullOrEmpty(ifToken))
        {
            // Resource is locked and caller presented no token → 423 Locked. This covers both the
            // "someone else's lock" case and the "forgot to send If:" case — both rightly refuse.
            return false;
        }

        // Token + owner match → write permitted through the lock. The owner check is belt-and-
        // suspenders (the token is already a capability), but the layered check makes log-leak
        // scenarios harder to exploit: an attacker who observed Lock-Token in a forwarded header
        // log can't reuse it from a different user account.
        return active.Token == ifToken && active.OwnerId == ownerId;
    }

    /// <summary>
    /// Cryptographically-secure 128-bit token, hex-encoded and wrapped as a <c>urn:uuid:</c> URI
    /// for RFC 4918 compliance. See class-level xmldoc for why this is NOT
    /// <see cref="Guid.NewGuid"/>.
    /// </summary>
    public static string GenerateSecureToken()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return $"urn:uuid:{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    // Equality-match on the constraint name (not substring) to discriminate the active-lock
    // conflict from unrelated DB errors. Substring-matching on "FileLocks" would previously have
    // silently treated a future PK-violation or token-index rename as a lock conflict and
    // returned a ghost Conflict result to the caller. Mirrors the AuditEntryConsumer +
    // QuotaNotificationConsumer pattern.
    private static bool IsActiveLockConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg
        && pg.SqlState == "23505"
        && pg.ConstraintName == FileLockConstraintNames.ActiveLockUniqueIndex;
}
