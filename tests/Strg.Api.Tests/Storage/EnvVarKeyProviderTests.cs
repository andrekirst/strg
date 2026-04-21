using System.Security.Cryptography;
using FluentAssertions;
using Strg.Infrastructure.Storage;
using Xunit;

namespace Strg.Api.Tests.Storage;

/// <summary>
/// The KEK plumbing has to be right before any downstream crypto code gets written — a bug here
/// silently corrupts every DEK and the only symptom is "decrypt throws auth-tag-mismatch on all
/// files". These tests pin the fail-fast startup contract, the roundtrip contract, and the
/// tampering-detection contract so any future change has to earn its place against them.
/// </summary>
public sealed class EnvVarKeyProviderTests
{
    private static readonly byte[] ValidKek = new byte[]
    {
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10,
        0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
        0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f, 0x20,
    };

    private static string ValidKekBase64 => Convert.ToBase64String(ValidKek);

    [Fact]
    public void Constructor_throws_when_env_var_is_missing()
    {
        var act = () => new EnvVarKeyProvider(base64Kek: null);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{EnvVarKeyProvider.EnvVarName}*");
    }

    [Fact]
    public void Constructor_throws_when_env_var_is_empty()
    {
        var act = () => new EnvVarKeyProvider(base64Kek: "");
        act.Should().Throw<InvalidOperationException>();
    }

    // The empty-string case goes through the same IsNullOrWhiteSpace guard, but a whitespace-only
    // input is the specific shape an operator tends to produce by accident (copy-paste newline,
    // a YAML quirk that turns a blank line into a space-only value). Pinning it explicitly makes
    // the guard's intent visible and catches a future refactor that narrows to IsNullOrEmpty.
    [Fact]
    public void Constructor_throws_when_env_var_is_whitespace_only()
    {
        var act = () => new EnvVarKeyProvider(base64Kek: "   \t\n  ");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{EnvVarKeyProvider.EnvVarName}*");
    }

    // All-zero KEK: a zero-initialised buffer accidentally base64-encoded is the most common
    // shape of a "KEK not actually generated" misconfig. It passes the length check but defeats
    // the envelope. The explicit guard in the constructor keeps the server from starting with
    // a provably-useless key, even at the cost of rejecting the (vanishingly improbable) case
    // of a genuine CSPRNG producing 32 zero bytes.
    [Fact]
    public void Constructor_throws_when_kek_is_all_zero()
    {
        var allZero = Convert.ToBase64String(new byte[32]);
        var act = () => new EnvVarKeyProvider(base64Kek: allZero);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*all-zero*");
    }

    [Fact]
    public void Constructor_throws_when_env_var_is_not_base64()
    {
        var act = () => new EnvVarKeyProvider(base64Kek: "not-base64!!!");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not valid base64*");
    }

    [Fact]
    public void Constructor_throws_when_kek_is_wrong_length()
    {
        // 16 bytes of base64 — would be AES-128. Must reject.
        var shortKek = Convert.ToBase64String(new byte[16]);
        var act = () => new EnvVarKeyProvider(base64Kek: shortKek);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*32 bytes*");
    }

    [Fact]
    public void GenerateDataKey_returns_32_bytes()
    {
        var provider = new EnvVarKeyProvider(ValidKekBase64);
        var dek = provider.GenerateDataKey();
        dek.Should().HaveCount(32);
    }

    [Fact]
    public void GenerateDataKey_returns_unique_values()
    {
        // Probability of collision on 32 random bytes is cryptographically negligible — a
        // collision here would mean RandomNumberGenerator is broken or not being called.
        var provider = new EnvVarKeyProvider(ValidKekBase64);
        var a = provider.GenerateDataKey();
        var b = provider.GenerateDataKey();
        a.Should().NotEqual(b);
    }

    [Fact]
    public void EncryptDek_then_DecryptDek_roundtrips()
    {
        var provider = new EnvVarKeyProvider(ValidKekBase64);
        var dek = provider.GenerateDataKey();

        var wrapped = provider.EncryptDek(dek);
        var unwrapped = provider.DecryptDek(wrapped);

        unwrapped.Should().Equal(dek);
    }

    [Fact]
    public void EncryptDek_produces_different_ciphertext_each_call()
    {
        // Random nonce per call — two wraps of the same DEK must NOT produce identical envelopes,
        // otherwise an observer seeing the file_keys table can infer which files share a DEK.
        var provider = new EnvVarKeyProvider(ValidKekBase64);
        var dek = provider.GenerateDataKey();

        var wrapA = provider.EncryptDek(dek);
        var wrapB = provider.EncryptDek(dek);

        wrapA.Should().NotEqual(wrapB);
    }

    [Fact]
    public void EncryptDek_envelope_is_nonce_plus_ciphertext_plus_tag()
    {
        var provider = new EnvVarKeyProvider(ValidKekBase64);
        var dek = provider.GenerateDataKey();

        var wrapped = provider.EncryptDek(dek);

        // 12 (nonce) + 32 (ciphertext) + 16 (tag) = 60 bytes. Any change here is a wire-layout
        // break that needs a migration for existing file_keys rows.
        wrapped.Should().HaveCount(60);
    }

    [Fact]
    public void EncryptDek_rejects_wrong_length_input()
    {
        var provider = new EnvVarKeyProvider(ValidKekBase64);
        var act = () => provider.EncryptDek(new byte[16]);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*32 bytes*");
    }

    [Fact]
    public void DecryptDek_detects_ciphertext_tampering()
    {
        var provider = new EnvVarKeyProvider(ValidKekBase64);
        var dek = provider.GenerateDataKey();
        var wrapped = provider.EncryptDek(dek);

        // Flip a byte in the ciphertext portion — AES-GCM auth tag must reject this.
        wrapped[20] ^= 0xFF;

        var act = () => provider.DecryptDek(wrapped);
        act.Should().Throw<AuthenticationTagMismatchException>();
    }

    [Fact]
    public void DecryptDek_detects_tag_tampering()
    {
        var provider = new EnvVarKeyProvider(ValidKekBase64);
        var dek = provider.GenerateDataKey();
        var wrapped = provider.EncryptDek(dek);

        // Flip a byte in the tag — the envelope itself flags integrity failure.
        wrapped[^1] ^= 0xFF;

        var act = () => provider.DecryptDek(wrapped);
        act.Should().Throw<AuthenticationTagMismatchException>();
    }

    [Fact]
    public void DecryptDek_rejects_wrapped_dek_from_different_kek()
    {
        // Two providers with different KEKs — an envelope from one cannot be unwrapped by the
        // other. This is the "wrong KEK / rotated KEK" failure mode surfacing cleanly.
        var providerA = new EnvVarKeyProvider(ValidKekBase64);

        var differentKek = new byte[32];
        RandomNumberGenerator.Fill(differentKek);
        var providerB = new EnvVarKeyProvider(Convert.ToBase64String(differentKek));

        var wrapped = providerA.EncryptDek(providerA.GenerateDataKey());

        var act = () => providerB.DecryptDek(wrapped);
        act.Should().Throw<AuthenticationTagMismatchException>();
    }

    [Fact]
    public void DecryptDek_rejects_truncated_envelope()
    {
        var provider = new EnvVarKeyProvider(ValidKekBase64);
        var dek = provider.GenerateDataKey();
        var wrapped = provider.EncryptDek(dek);

        // Drop the tag — AesGcm can't run Decrypt without exactly the tag-length bytes of tag,
        // so this is caught by the explicit length check before AesGcm is even touched.
        var truncated = wrapped[..^1];

        var act = () => provider.DecryptDek(truncated);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*60 bytes*");
    }

    // Concurrency smoke: the provider is registered as a DI singleton and will be called from
    // every upload/download request in parallel. Each EncryptDek constructs a fresh AesGcm, so
    // the only shared state is the read-only _kek buffer — but a regression that accidentally
    // introduces per-instance mutable state (shared nonce buffer, cached cipher) would corrupt
    // envelopes under load and the symptom would be AuthenticationTagMismatchException under
    // concurrent traffic only. 1000 iterations is enough to make a race reliably visible without
    // making the test slow.
    [Fact]
    public void EncryptDek_handles_concurrent_calls()
    {
        var provider = new EnvVarKeyProvider(ValidKekBase64);
        const int iterations = 1000;

        Parallel.For(0, iterations, _ =>
        {
            var dek = provider.GenerateDataKey();
            var wrapped = provider.EncryptDek(dek);
            var unwrapped = provider.DecryptDek(wrapped);
            unwrapped.Should().Equal(dek);
        });
    }

    [Fact]
    public void GenerateDataKey_throws_after_dispose()
    {
        var provider = new EnvVarKeyProvider(ValidKekBase64);
        provider.Dispose();

        var act = () => provider.GenerateDataKey();
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void EncryptDek_throws_after_dispose()
    {
        var provider = new EnvVarKeyProvider(ValidKekBase64);
        var dek = provider.GenerateDataKey();
        provider.Dispose();

        // After Dispose, _kek is zeroed — if the disposal guard were missing, EncryptDek would
        // silently produce an envelope under an all-zero KEK, and every future DecryptDek with
        // the real KEK would fail. Throwing here is the fail-fast signal.
        var act = () => provider.EncryptDek(dek);
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void DecryptDek_throws_after_dispose()
    {
        var provider = new EnvVarKeyProvider(ValidKekBase64);
        var dek = provider.GenerateDataKey();
        var wrapped = provider.EncryptDek(dek);
        provider.Dispose();

        var act = () => provider.DecryptDek(wrapped);
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        // DI containers generally dispose singletons exactly once, but a double-dispose must not
        // throw — otherwise a test-harness ServiceProvider with a manual Dispose followed by
        // container shutdown would crash on the way down.
        var provider = new EnvVarKeyProvider(ValidKekBase64);
        provider.Dispose();

        var act = () => provider.Dispose();
        act.Should().NotThrow();
    }
}
