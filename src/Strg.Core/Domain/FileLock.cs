namespace Strg.Core.Domain;

/// <summary>
/// STRG-072 — a WebDAV LOCK held against an in-drive resource URI. Persisted rather than held in
/// memory so that (a) locks survive process recycles (Kubernetes pod churn, blue/green deploys) and
/// (b) multi-instance deployments behind a load balancer see the same lock state — an in-memory
/// lock manager would let two replicas simultaneously hand out write-locks on the same file, which
/// is the exact failure mode WebDAV class-2 locks are supposed to prevent.
///
/// <para><b>Token is security-relevant, not just an identifier.</b> Whoever holds the token can
/// unlock the resource or reuse the lock for subsequent PUTs via <c>If:</c> headers. Tokens are
/// generated from <c>RandomNumberGenerator.GetBytes(16)</c> (128-bit CSPRNG output) and formatted
/// as <c>urn:uuid:{hex}</c> — <b>not</b> <c>Guid.NewGuid()</c>. <see cref="Guid.NewGuid"/> on .NET
/// is sourced from a CSPRNG in practice, but the documented contract is only that GUIDs are
/// unique, not unguessable, and a future framework change to a faster non-crypto generator
/// (think: the v7 time-ordered variant) would silently weaken this surface. Pinning the generator
/// explicitly keeps the guarantee load-bearing.</para>
///
/// <para><b>Race-safe atomic insert.</b> The naive "query-then-insert" pattern has a TOCTOU window
/// where two concurrent LOCKs both see no active lock and both succeed. The defence is a partial
/// unique index on <c>(TenantId, ResourceUri) WHERE ExpiresAt &gt; NOW()</c> — see
/// <c>FileLockConfiguration</c>. Expired rows are excluded from the uniqueness scope so a stale
/// token from last week doesn't block today's LOCK, and the DB enforces at-most-one-active-lock
/// even under perfectly simultaneous writes.</para>
///
/// <para><b>Mutable <see cref="ExpiresAt"/>.</b> RFC 4918 §10.7 allows clients to refresh a lock
/// they already hold via <c>LOCK</c> with an <c>If:</c> header naming the existing token — we
/// implement this by updating <c>ExpiresAt</c> in-place rather than deleting + reinserting, which
/// keeps the token stable (clients rely on the token identity across refreshes) and avoids
/// spurious unique-index contention on the refresh path.</para>
/// </summary>
public sealed class FileLock : TenantedEntity
{
    /// <summary>
    /// In-drive resource URI — shape <c>{driveName}/{path}</c>. We don't store the raw
    /// <c>/dav/{drive}/{path}</c> request path because the <c>/dav</c> prefix is a routing
    /// detail; keeping the URI drive-rooted means a future mount-point rename wouldn't strand
    /// live locks.
    /// </summary>
    public required string ResourceUri { get; init; }

    /// <summary>
    /// Opaque lock token returned to the client and echoed in the <c>Lock-Token</c> response
    /// header. Shape is <c>urn:uuid:{32-hex}</c>. Stored as-is (no hashing) — unlike passwords,
    /// the plaintext token is what subsequent requests present, so a hash wouldn't help and would
    /// just break the comparison.
    /// </summary>
    public required string Token { get; init; }

    /// <summary>
    /// Principal that owns the lock — the authenticated user's id from the JWT <c>sub</c> claim.
    /// UNLOCK and overwriting PUTs must present a token that the owner issued; this field is the
    /// enforcement hook for "you can't steal someone else's lock".
    /// </summary>
    public required Guid OwnerId { get; init; }

    /// <summary>
    /// Human-readable owner info from the LOCK request body's <c>&lt;owner&gt;</c> element (often
    /// a username or machine name). Echoed back verbatim in PROPFIND <c>lockdiscovery</c> responses
    /// so other clients viewing the resource see who holds the lock. Nullable because RFC 4918
    /// §14.17 makes the element optional.
    /// </summary>
    public string? OwnerInfo { get; init; }

    /// <summary>
    /// Wall-clock expiry. Mutable so that refresh-lock can extend without rotating the token.
    /// The partial unique index filters on this column, so expired rows fall out of uniqueness
    /// automatically — a sweeper isn't required for correctness (only for storage hygiene).
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
