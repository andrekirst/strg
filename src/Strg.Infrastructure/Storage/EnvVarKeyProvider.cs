using System.Security.Cryptography;
using Strg.Core.Storage;

namespace Strg.Infrastructure.Storage;

/// <summary>
/// <see cref="IKeyProvider"/> that reads the KEK from the <c>STRG_SECURITY__ENCRYPTIONKEY</c>
/// environment variable (base64-encoded 32 bytes). The env-var form is the minimum-viable
/// KEK source for v0.1 — suitable for self-hosted deployments where the operator controls the
/// process environment and does NOT want to operate a KMS.
///
/// <para><b>Wire layout of <see cref="IKeyProvider.EncryptDek"/> output:</b>
/// <c>nonce(12) || ciphertext(32) || tag(16)</c> — 60 bytes total for a 32-byte DEK. This
/// matches the AES-GCM recommended nonce length (96 bits) and the default tag length (128 bits).
/// Callers MUST treat the byte array as opaque; any change to this layout is a breaking change
/// for the <c>file_keys</c> table and will require a migration.</para>
///
/// <para><b>Fail-fast on startup.</b> The constructor validates the env var and throws if it
/// is missing, not base64, not exactly 32 bytes, or all-zero. The all-zero guard catches the
/// common operator misconfig where a zero-initialised buffer is accidentally base64-encoded
/// into the env var; such a KEK would produce a valid AES-GCM envelope but with zero entropy.
/// An encryption-enabled Drive must not limp along with a misconfigured KEK and silently
/// corrupt DEKs at write time.</para>
///
/// <para><b>Operator responsibility: CSPRNG-generated KEK.</b> The 32-byte KEK MUST be generated
/// with a cryptographically-secure random source — e.g., <c>openssl rand -base64 32</c> or
/// <c>head -c 32 /dev/urandom | base64</c>. A predictable KEK (password-derived without a KDF,
/// deterministic seed, non-random buffer) defeats the entire at-rest encryption envelope.
/// The all-zero guard catches the most visible misconfig but CANNOT detect low-entropy inputs.</para>
///
/// <para><b>Env-var co-residency caveat.</b> Once loaded, the KEK material lives in managed
/// memory (<c>_kek</c>) AND continues to be visible in the process environment block:
/// <c>/proc/&lt;pid&gt;/environ</c> on Linux, <c>ps eww</c>, <c>docker inspect</c>'s
/// <c>Config.Env</c>, and crash dumps that capture the env block. This is an intrinsic
/// limitation of env-var-based secret delivery; operators who need stronger isolation (the KEK
/// not reachable by sibling processes on the same host) should use a KMS-backed
/// <see cref="IKeyProvider"/> implementation, not this one. <see cref="Dispose"/> zeros the
/// managed copy but does NOT clear the env-var block — the caller would need to also
/// <c>Environment.SetEnvironmentVariable(EnvVarName, null)</c> which is a separate concern and
/// racy against concurrent readers of the environment.</para>
///
/// <para><b>Disposal.</b> The class implements <see cref="IDisposable"/> and zeros <c>_kek</c>
/// on dispose via <see cref="CryptographicOperations.ZeroMemory"/>. After disposal, calls to
/// <see cref="EncryptDek"/> and <see cref="DecryptDek"/> throw
/// <see cref="ObjectDisposedException"/> rather than silently producing enciphered output with a
/// zeroed key. Registered as a DI singleton, so the container handles disposal at process
/// shutdown — manual disposal is not expected in normal use. Note: <c>_kek</c> is a managed
/// array; the GC may relocate it during compaction and <see cref="CryptographicOperations.ZeroMemory"/>
/// scrubs only the current location. Prior relocated copies may persist in freed heap pages until
/// reused. This is a known managed-memory limitation, strictly weaker than the env-var
/// co-residency exposure already documented above.</para>
/// </summary>
public sealed class EnvVarKeyProvider : IKeyProvider, IDisposable
{
    public const string EnvVarName = "STRG_SECURITY__ENCRYPTIONKEY";
    private const int KekLengthBytes = 32;
    private const int DekLengthBytes = 32;
    private const int NonceLengthBytes = 12;
    private const int TagLengthBytes = 16;

    private readonly byte[] _kek;
    private bool _disposed;

    public EnvVarKeyProvider()
        : this(Environment.GetEnvironmentVariable(EnvVarName))
    {
    }

    // Internal ctor for tests — avoids requiring env-var mutation for unit tests. Production
    // code always goes through the parameterless ctor so the env-var contract is authoritative.
    internal EnvVarKeyProvider(string? base64Kek)
    {
        if (string.IsNullOrWhiteSpace(base64Kek))
        {
            throw new InvalidOperationException(
                $"Environment variable '{EnvVarName}' is not set. An encryption-enabled drive requires "
                + "a base64-encoded 32-byte KEK. Refuse to start rather than write undecryptable data.");
        }

        byte[] kek;
        try
        {
            kek = Convert.FromBase64String(base64Kek);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"Environment variable '{EnvVarName}' is not valid base64.", ex);
        }

        if (kek.Length != KekLengthBytes)
        {
            throw new InvalidOperationException(
                $"Environment variable '{EnvVarName}' decoded to {kek.Length} bytes; "
                + $"AES-256 requires exactly {KekLengthBytes} bytes.");
        }

        // All-zero KEK = operator misconfig. A zero-initialised buffer accidentally base64-encoded
        // into the env var would produce a valid-shape but zero-entropy KEK. The explicit loop
        // (vs LINQ All) avoids any iterator boxing of the secret buffer. FixedTimeEquals is used
        // for habit even though timing doesn't matter at startup — it's still a secret-material
        // comparison and unconditional branches on secrets are a bad pattern to establish.
        if (CryptographicOperations.FixedTimeEquals(kek, new byte[KekLengthBytes]))
        {
            throw new InvalidOperationException(
                $"Environment variable '{EnvVarName}' decoded to an all-zero buffer. "
                + "Generate a cryptographically-random KEK (e.g., 'openssl rand -base64 32' or "
                + "'head -c 32 /dev/urandom | base64') — a zero KEK defeats the at-rest envelope.");
        }

        _kek = kek;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        CryptographicOperations.ZeroMemory(_kek);
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public byte[] GenerateDataKey()
    {
        ThrowIfDisposed();
        return RandomNumberGenerator.GetBytes(DekLengthBytes);
    }

    public byte[] EncryptDek(byte[] dek)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(dek);
        if (dek.Length != DekLengthBytes)
        {
            throw new ArgumentException(
                $"DEK must be exactly {DekLengthBytes} bytes; received {dek.Length}.", nameof(dek));
        }

        var envelope = new byte[NonceLengthBytes + dek.Length + TagLengthBytes];
        var nonce = envelope.AsSpan(0, NonceLengthBytes);
        var ciphertext = envelope.AsSpan(NonceLengthBytes, dek.Length);
        var tag = envelope.AsSpan(NonceLengthBytes + dek.Length, TagLengthBytes);

        RandomNumberGenerator.Fill(nonce);

        // AesGcm owns the KEK for the scope of the call; Dispose zeros the expanded key schedule.
        using var aes = new AesGcm(_kek, TagLengthBytes);
        aes.Encrypt(nonce, dek, ciphertext, tag);

        return envelope;
    }

    public byte[] DecryptDek(byte[] encryptedDek)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(encryptedDek);
        var expected = NonceLengthBytes + DekLengthBytes + TagLengthBytes;
        if (encryptedDek.Length != expected)
        {
            throw new ArgumentException(
                $"Wrapped DEK envelope must be {expected} bytes; received {encryptedDek.Length}. "
                + "Possible corruption or a wire-layout change not covered by migration.",
                nameof(encryptedDek));
        }

        var nonce = encryptedDek.AsSpan(0, NonceLengthBytes);
        var ciphertext = encryptedDek.AsSpan(NonceLengthBytes, DekLengthBytes);
        var tag = encryptedDek.AsSpan(NonceLengthBytes + DekLengthBytes, TagLengthBytes);
        var plaintext = new byte[DekLengthBytes];

        using var aes = new AesGcm(_kek, TagLengthBytes);
        // Throws AuthenticationTagMismatchException if the envelope was tampered with OR if the
        // KEK has changed — both failure modes surface as "cannot unwrap this DEK", which is
        // what callers need to know.
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }
}
