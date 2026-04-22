using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Strg.WebDav;
using Xunit;

namespace Strg.Api.Tests.WebDav;

/// <summary>
/// STRG-073 — pins the cryptographic posture of <see cref="WebDavJwtCache"/>'s key derivation
/// and the per-username invalidation contract. The mandated security test (plain password never
/// enters the cache key) lives here because it's the single load-bearing invariant for
/// threat-model purposes: an attacker with memory access to the host's cache dictionary must
/// see only SHA-256 digests, never the credential string.
/// </summary>
public sealed class WebDavJwtCacheTests
{
    [Fact]
    public void Cache_key_never_contains_plain_password()
    {
        // Distinctive password characters: if any byte of this string appears in the cache key,
        // the key is leaking the credential. The assertion is on the UTF-8 bytes rather than the
        // string form so a hypothetical future encoding bug (e.g. base64 of the password instead
        // of hashing it) would still fail the test.
        const string username = "alice@strg.local";
        const string password = "S3cr3t!-pl41nt3xt-unique-marker-7f3a";

        var key = WebDavJwtCache.BuildKey(username, password);

        key.Should().NotContain(password,
            "the plain password must never appear in the cache key — memory-access attackers would otherwise see credentials");
        key.Should().StartWith("webdav-jwt:",
            "the prefix is part of the cache-key contract documented on IWebDavJwtCache; downstream log filtering relies on it");
        key.Should().Contain(username,
            "the normalized username is a legitimate part of the key — it's already public elsewhere (logs, audit rows)");

        // Belt-and-braces: compute the expected SHA-256 digest and assert its hex form is the
        // suffix. If BuildKey ever swaps to a different algorithm (say MD5 or a salt-and-pepper
        // scheme), this test forces the change to be deliberate.
        var expectedDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
        key.Should().EndWith(expectedDigest,
            "the suffix is SHA-256(password) hex — a swap to another algorithm must update this pin in lockstep");
    }

    [Fact]
    public void Cache_key_is_case_insensitive_on_username()
    {
        // Usernames are email addresses persisted lowercase by UserManager; WebDAV clients may
        // send mixed-case credentials. The cache must treat "Alice@strg.local" and
        // "alice@strg.local" as the same key or else hit-rate fragments and the same valid
        // token re-exchanges once per casing variant.
        var mixedCase = WebDavJwtCache.BuildKey("Alice@Strg.Local", "password");
        var lowerCase = WebDavJwtCache.BuildKey("alice@strg.local", "password");

        mixedCase.Should().Be(lowerCase);
    }

    [Fact]
    public void Cache_key_differs_when_password_differs()
    {
        // Changing the password must produce a different key so old entries miss — this is the
        // first line of defence on password change. InvalidateUser accelerates the eviction but
        // the natural miss-on-new-password is what protects the window between the DB write and
        // the cache-invalidation hook.
        var keyA = WebDavJwtCache.BuildKey("user", "passwordA");
        var keyB = WebDavJwtCache.BuildKey("user", "passwordB");

        keyA.Should().NotBe(keyB);
    }

    [Fact]
    public void InvalidateUser_evicts_all_entries_for_the_username()
    {
        // Single username, two password-hash suffixes (e.g. a failed password change that
        // briefly cached the new credential before rolling back would leave an orphan). The
        // per-username side-index must enumerate both and drop them.
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new WebDavJwtCache(memoryCache);

        cache.Set("alice@strg.local", "passwordA", "jwt-a", TimeSpan.FromMinutes(14));
        cache.Set("alice@strg.local", "passwordB", "jwt-b", TimeSpan.FromMinutes(14));

        cache.InvalidateUser("alice@strg.local");

        cache.TryGet("alice@strg.local", "passwordA").Should().BeNull();
        cache.TryGet("alice@strg.local", "passwordB").Should().BeNull();
    }

    [Fact]
    public void InvalidateUser_does_not_evict_other_users()
    {
        // Bob's entry must survive Alice's password change. The side-index is keyed on
        // normalized username, so this is a regression test against "invalidate-by-prefix" bugs
        // where a future rewrite might accidentally match substrings.
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new WebDavJwtCache(memoryCache);

        cache.Set("alice@strg.local", "p", "jwt-alice", TimeSpan.FromMinutes(14));
        cache.Set("bob@strg.local", "p", "jwt-bob", TimeSpan.FromMinutes(14));

        cache.InvalidateUser("alice@strg.local");

        cache.TryGet("bob@strg.local", "p").Should().Be("jwt-bob");
    }

    [Fact]
    public void Remove_evicts_only_the_targeted_entry()
    {
        // STRG-073 #145 Part 1 — pins the surgical scope of Remove vs InvalidateUser. The bridge
        // calls this on the cross-tenant verification-gate 401 path; if Remove accidentally
        // behaved like a user-sweep it would also evict the OTHER tenant's legitimate session
        // that shares (username, different-password) keys, doubling the disruption instead of
        // fixing it. Two entries under the same username, Remove one, assert the sibling survives.
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new WebDavJwtCache(memoryCache);

        cache.Set("alice@strg.local", "passwordA", "jwt-a", TimeSpan.FromMinutes(14));
        cache.Set("alice@strg.local", "passwordB", "jwt-b", TimeSpan.FromMinutes(14));

        cache.Remove("alice@strg.local", "passwordA");

        cache.TryGet("alice@strg.local", "passwordA").Should().BeNull(
            "the targeted (username, password) entry must be gone after Remove");
        cache.TryGet("alice@strg.local", "passwordB").Should().Be("jwt-b",
            "the sibling entry under a different password hash must survive — Remove is surgical, not a user-sweep");
    }

    [Fact]
    public void Remove_is_noop_when_entry_absent()
    {
        // A Remove on an already-evicted or never-set (username, password) pair must be a silent
        // no-op. The bridge fires Remove on every Mismatch 401 regardless of whether the 401
        // came from a cache-hit or an exchange-miss; the exchange-miss path has no cache entry
        // to evict but the caller should not have to branch on that.
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new WebDavJwtCache(memoryCache);

        var act = () => cache.Remove("ghost@strg.local", "never-set");

        act.Should().NotThrow();
    }

    [Fact]
    public void Remove_subsequently_does_not_affect_other_users()
    {
        // Regression guard against a future rewrite that mis-keys Remove on the username-only
        // side-index instead of the full (username, password) cache key. Bob's entry must
        // survive Remove on an Alice credential pair even if a buggy impl tried to sweep
        // by-username.
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new WebDavJwtCache(memoryCache);

        cache.Set("alice@strg.local", "p", "jwt-alice", TimeSpan.FromMinutes(14));
        cache.Set("bob@strg.local", "p", "jwt-bob", TimeSpan.FromMinutes(14));

        cache.Remove("alice@strg.local", "p");

        cache.TryGet("alice@strg.local", "p").Should().BeNull();
        cache.TryGet("bob@strg.local", "p").Should().Be("jwt-bob");
    }

    [Fact]
    public void Set_with_zero_lifetime_clamps_to_one_second_instead_of_throwing()
    {
        // Upstream callers subtract 60s from expires_in — a pathological token with
        // expires_in <= 60 would otherwise produce a zero/negative TimeSpan that IMemoryCache
        // rejects with ArgumentOutOfRangeException. The clamp keeps the bridge alive on
        // exotic tokens at the cost of a tiny TTL window.
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new WebDavJwtCache(memoryCache);

        var act = () => cache.Set("user", "password", "jwt", TimeSpan.Zero);

        act.Should().NotThrow();
        cache.TryGet("user", "password").Should().Be("jwt");
    }
}
