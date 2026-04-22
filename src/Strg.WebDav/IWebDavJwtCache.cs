namespace Strg.WebDav;

/// <summary>
/// STRG-073 — cache surface consumed by <see cref="BasicAuthJwtBridgeMiddleware"/> to reuse JWT
/// access tokens across consecutive WebDAV requests from the same client credentials. Decouples
/// the bridge from the underlying <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>
/// so the <c>changePassword</c> GraphQL mutation can call <see cref="InvalidateUser"/> without
/// threading the memory-cache implementation through GraphQL types.
///
/// <para><b>Cache key shape.</b> <c>webdav-jwt:{username}:{HEX(SHA256(password))}</c> — the plain
/// password never enters the cache or any log line. SHA-256 is not intended as a password hash
/// (the canonical PBKDF2 pass lives in <c>UserManager</c>); it is a key-derivation one-way
/// function so that an attacker with memory access sees only the hash, not the credential. A
/// password change produces a different key and misses the existing cache entry, but the old
/// entry would still serve until natural TTL — which is why
/// <see cref="InvalidateUser"/> flushes all entries keyed by that username.</para>
///
/// <para><b>TTL is caller-driven.</b> The bridge receives <c>expires_in</c> from the token
/// response and subtracts 60 seconds before calling <see cref="Set"/>. The cache does not
/// inspect the JWT or know the server-side access-token lifetime — that coupling would break the
/// moment a per-request lifetime override ships.</para>
/// </summary>
public interface IWebDavJwtCache
{
    /// <summary>
    /// Returns the cached JWT for the <c>(username, password)</c> pair, or <c>null</c> on miss
    /// or TTL expiry. Does not extend the lifetime of hit entries — the TTL is absolute from
    /// <see cref="Set"/> time so a busy client cannot indefinitely keep a stale token alive past
    /// a password change that happened between issuance and eviction.
    /// </summary>
    string? TryGet(string username, string password);

    /// <summary>
    /// Stores the JWT against the <c>(username, password)</c> pair with absolute expiry of
    /// <paramref name="lifetime"/>. The caller is responsible for subtracting the 60-second
    /// safety margin from <c>expires_in</c> before calling.
    /// </summary>
    void Set(string username, string password, string jwt, TimeSpan lifetime);

    /// <summary>
    /// Evicts every cached entry keyed by <paramref name="username"/>, regardless of which
    /// password-hash suffix they carry. Called from <c>changePassword</c> after the new password
    /// hash has been committed; stale entries would otherwise continue serving access tokens
    /// minted for the previous credential until natural TTL.
    /// </summary>
    void InvalidateUser(string username);
}
