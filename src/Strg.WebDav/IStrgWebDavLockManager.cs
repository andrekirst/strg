using Strg.Core.Domain;

namespace Strg.WebDav;

/// <summary>
/// STRG-072 — owns the lifecycle of RFC 4918 class-2 locks. Intentionally <i>not</i> NWebDav's
/// <c>ILockManager</c>: STRG-068 pinned the reasoning that the <c>NWebDav.Server.AspNetCore</c>
/// adapter is transitively vulnerable (GHSA-hxrm-9w7p-39cc) and its <c>IHttpContext</c>
/// abstraction is abandoned. Mirroring <see cref="IStrgWebDavStore"/>, we consume only the core
/// <c>NWebDav.Server</c> package and expose a native contract that middleware + tests can depend
/// on without dragging the ASP.NET Core adapter into the graph. ArchTest #118 guards against a
/// future PR silently re-introducing that package.
///
/// <para><b>ResourceUri shape.</b> Callers pass drive-scoped paths — <c>{driveName}/{inDrivePath}</c>
/// — not the raw <c>/dav/...</c> request path. The <c>/dav</c> prefix is a routing concern and a
/// future prefix-rename shouldn't strand live locks.</para>
/// </summary>
public interface IStrgWebDavLockManager
{
    /// <summary>
    /// Attempts to acquire an exclusive write-lock on <paramref name="resourceUri"/>.
    ///
    /// <para><b>Race semantics.</b> Implementations MUST rely on the partial unique index
    /// <c>(TenantId, ResourceUri) WHERE ExpiresAt &gt; NOW()</c> for atomicity — a query-then-insert
    /// check has a TOCTOU window that query-then-INSERT cannot close. When a concurrent LOCK wins
    /// the race, the losing call returns <see cref="LockResult.Conflict"/> with the winning
    /// lock-owner surface (via <see cref="GetLockAsync"/>) so the middleware can emit 423 Locked.</para>
    ///
    /// <para><b>Token entropy.</b> <paramref name="tokenFactory"/> is called once per successful
    /// acquisition. Production passes <see cref="DbLockManager.GenerateSecureToken"/>, which is
    /// <c>RandomNumberGenerator.GetBytes(16)</c> hex-encoded — NOT <see cref="Guid.NewGuid"/>. The
    /// factory shape lets tests inject deterministic tokens without undermining the crypto
    /// guarantee in prod.</para>
    /// </summary>
    Task<LockResult> LockAsync(
        string resourceUri,
        Guid ownerId,
        string? ownerInfo,
        TimeSpan timeout,
        Func<string>? tokenFactory = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a lock, but ONLY if <paramref name="token"/> matches the active lock's token.
    /// Token mismatch or expired-lock returns <c>false</c> (middleware maps to 409 Conflict per
    /// RFC 4918 §9.11). We don't require <paramref name="ownerId"/> to match because lock
    /// tokens are already capability tokens — possession IS authorization. That said, the
    /// owner check is still load-bearing: an attacker who somehow saw a Lock-Token header in a
    /// log could otherwise unlock someone else's resource, so we pin both.
    /// </summary>
    Task<bool> UnlockAsync(string resourceUri, string token, Guid ownerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends <paramref name="token"/>'s <c>ExpiresAt</c> to <c>now + timeout</c>. Refresh keeps
    /// the token stable — clients rely on the token identity across the LOCK-with-If-header
    /// refresh dance. Returns <c>null</c> if no matching <i>active</i> lock exists (expired, wrong
    /// token, wrong owner), letting the middleware shape the 412/409 response.
    /// </summary>
    Task<FileLock?> RefreshAsync(string resourceUri, string token, Guid ownerId, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the active lock on <paramref name="resourceUri"/> or <c>null</c> if none exists.
    /// "Active" means <c>ExpiresAt &gt; NOW()</c> — expired rows are ignored (they'll be cleaned
    /// by the sweeper, but in the meantime they're indistinguishable from "no lock").
    /// </summary>
    Task<FileLock?> GetLockAsync(string resourceUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write-gate check used by PUT / DELETE / MOVE / COPY / PROPPATCH — returns true if the
    /// caller MAY write. Ungated when no lock exists; gated when a lock exists and the caller
    /// either (a) isn't the owner, (b) doesn't present the matching token, or (c) presents a
    /// token that has expired. Presenting the right token to your own lock lets you write through
    /// it — the whole point of lock tokens is to serialize writes from a single client, not block
    /// them.
    /// </summary>
    Task<bool> CanWriteAsync(string resourceUri, Guid ownerId, string? ifToken, CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of <see cref="IStrgWebDavLockManager.LockAsync"/>.
/// </summary>
public sealed record LockResult(LockStatus Status, FileLock? Lock)
{
    public static LockResult Acquired(FileLock fileLock) => new(LockStatus.Acquired, fileLock);
    public static LockResult Conflict(FileLock existing) => new(LockStatus.Conflict, existing);
}

public enum LockStatus
{
    /// <summary>Lock row was inserted and is now active.</summary>
    Acquired,

    /// <summary>Another active lock exists — 423 Locked.</summary>
    Conflict,
}
