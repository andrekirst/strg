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
/// is missing, not base64, or not exactly 32 bytes. This is deliberate — an encryption-enabled
/// Drive must not limp along with a misconfigured KEK and silently corrupt DEKs at write time.</para>
/// </summary>
public sealed class EnvVarKeyProvider : IKeyProvider
{
    public const string EnvVarName = "STRG_SECURITY__ENCRYPTIONKEY";
    private const int KekLengthBytes = 32;
    private const int DekLengthBytes = 32;
    private const int NonceLengthBytes = 12;
    private const int TagLengthBytes = 16;

    private readonly byte[] _kek;

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

        _kek = kek;
    }

    public byte[] GenerateDataKey() => RandomNumberGenerator.GetBytes(DekLengthBytes);

    public byte[] EncryptDek(byte[] dek)
    {
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
