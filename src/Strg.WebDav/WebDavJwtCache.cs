using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace Strg.WebDav;

/// <summary>
/// STRG-073 — in-process <see cref="IWebDavJwtCache"/> backed by
/// <see cref="IMemoryCache"/>. Per-username key tracking enables targeted
/// <see cref="InvalidateUser"/> calls from the <c>changePassword</c> mutation without paying the
/// cost of a full cache sweep.
///
/// <para><b>Why track keys per username.</b> <see cref="IMemoryCache"/> provides no enumeration
/// API — a <see cref="InvalidateUser"/> call cannot "find every entry whose key starts with
/// <c>webdav-jwt:{username}:</c>" without a side-index. A <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// of username → live-cache-keys tracks exactly what the cache holds, so invalidation becomes a
/// single dictionary lookup followed by <c>Remove</c> per stored key. Stale side-index entries
/// (from TTL-expired cache rows) are pruned by an <see cref="ICacheEntry.PostEvictionCallbacks"/>
/// registration that tears the key back out of the index when the cache drops it.</para>
///
/// <para><b>Thread safety.</b> Singleton lifetime — all callers share one instance. The cache
/// itself is concurrent; the side-index uses <see cref="ConcurrentDictionary{TKey, TValue}"/>
/// with <see cref="HashSet{T}"/> values guarded by a <c>lock(set)</c> on modification. Race
/// between <c>Set</c> and <c>InvalidateUser</c> is benign in both directions: a
/// just-inserted-key that loses to a concurrent invalidate survives in the cache but is
/// orphaned from the index, and will be evicted by its TTL without further intervention; an
/// invalidation that races a set sees at worst the previous key set, but any key set after the
/// invalidate returns will still be in-flight and will serve a stale token briefly — the
/// caller pattern (invalidate AFTER <c>SaveChangesAsync</c>) tolerates this.</para>
/// </summary>
public sealed class WebDavJwtCache : IWebDavJwtCache
{
    private const string KeyPrefix = "webdav-jwt:";

    private readonly IMemoryCache _cache;
    private readonly ConcurrentDictionary<string, HashSet<string>> _userIndex = new(StringComparer.OrdinalIgnoreCase);

    public WebDavJwtCache(IMemoryCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    public string? TryGet(string username, string password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return null;
        }
        var key = BuildKey(username, password);
        return _cache.TryGetValue(key, out var value) ? value as string : null;
    }

    public void Set(string username, string password, string jwt, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentException.ThrowIfNullOrEmpty(jwt);
        // Absolute TTL minimum 1 second — a caller that already subtracted 60s from a short-lived
        // token could otherwise produce a zero or negative lifetime that AbsoluteExpirationRelativeToNow
        // rejects. Clamping to 1s means the entry exists just long enough for the current request to
        // read it back on the re-authentication pass in the same pipeline, then drops.
        var effective = lifetime <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : lifetime;

        var key = BuildKey(username, password);
        var indexKey = NormalizeUsername(username);

        var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = effective };
        options.RegisterPostEvictionCallback(OnEviction, indexKey);
        _cache.Set(key, jwt, options);

        var set = _userIndex.GetOrAdd(indexKey, _ => new HashSet<string>(StringComparer.Ordinal));
        lock (set)
        {
            set.Add(key);
        }
    }

    public void InvalidateUser(string username)
    {
        if (string.IsNullOrEmpty(username))
        {
            return;
        }
        var indexKey = NormalizeUsername(username);
        if (!_userIndex.TryRemove(indexKey, out var set))
        {
            return;
        }
        // Snapshot under the lock, then remove from cache outside it — _cache.Remove may call back
        // into OnEviction, and we want the set already detached from the index before then.
        string[] keys;
        lock (set)
        {
            keys = [.. set];
            set.Clear();
        }
        foreach (var key in keys)
        {
            _cache.Remove(key);
        }
    }

    private void OnEviction(object key, object? value, EvictionReason reason, object? state)
    {
        if (state is not string indexKey)
        {
            return;
        }
        // When InvalidateUser drives the eviction it already removed the entry from the side-index;
        // this callback fires for TTL/capacity evictions. TryGetValue keeps the lookup cheap on the
        // hot path and avoids mutating the dictionary when there's nothing to prune.
        if (!_userIndex.TryGetValue(indexKey, out var set))
        {
            return;
        }
        lock (set)
        {
            set.Remove((string)key);
        }
    }

    /// <summary>
    /// Cache key format: <c>webdav-jwt:{normalized-username}:{HEX(SHA256(password))}</c>.
    /// The password NEVER appears in the key; only its one-way SHA-256 digest does. A password
    /// change produces a completely different suffix, so old entries miss automatically on the
    /// next <see cref="TryGet"/> even before
    /// <see cref="InvalidateUser"/> runs — the invalidation hook just
    /// accelerates eviction and, critically, evicts entries whose <em>new</em> password hasn't
    /// rotated yet (e.g., pre-change entries for a password that was then set back to the old
    /// value would otherwise remain servable).
    /// </summary>
    internal static string BuildKey(string username, string password)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return $"{KeyPrefix}{NormalizeUsername(username)}:{Convert.ToHexString(digest)}";
    }

    // Usernames are email addresses in this codebase (UserManager lookup is email-keyed) and are
    // already persisted lowercase. Normalizing defensively here keeps the cache hit-rate stable
    // when a WebDAV client happens to send a mixed-case username — the token endpoint would
    // accept it because ValidateCredentialsAsync runs case-insensitively against the stored
    // lowercase Email, but the cache key would otherwise fragment across casing variants.
    private static string NormalizeUsername(string username) =>
        username.ToLowerInvariant();
}
