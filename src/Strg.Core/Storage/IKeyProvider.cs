namespace Strg.Core.Storage;

/// <summary>
/// Abstraction over the Key Encryption Key (KEK) — the long-lived master key that wraps the
/// per-file Data Encryption Keys (DEKs). Implementations fetch the KEK from their respective
/// backing store (env var, Vault, KMS, K8s Secret) and never expose it in raw form.
///
/// <para><b>Threat model.</b> DEKs are per-<see cref="Strg.Core.Domain.FileVersion"/>, written
/// to the <c>file_keys</c> table in wrapped form (<see cref="Strg.Core.Domain.FileKey.EncryptedDek"/>).
/// An attacker with filesystem access gets only ciphertext + wrapped DEKs; without the KEK
/// they cannot unwrap the DEK and therefore cannot decrypt the payload. This is the reason
/// the KEK MUST NOT be co-located with the database (env var / mounted secret / KMS — not a row).</para>
///
/// <para><b>Nonce safety.</b> The <see cref="EncryptDek"/> implementation owns nonce generation.
/// The caller never supplies a nonce — that would make nonce reuse the caller's responsibility
/// and nonce reuse is the single easiest way to destroy AES-GCM security.</para>
///
/// <para><b>Rotation.</b> v0.1 supports exactly one active KEK. Rotation lands in v0.2 and
/// will require a <c>KeyId</c> column on <see cref="Strg.Core.Domain.FileKey"/>; the interface
/// will grow a key-version return value then. Callers should treat the wrapped-DEK byte array
/// as opaque — the wire layout is an implementation detail of the <see cref="IKeyProvider"/>.</para>
/// </summary>
public interface IKeyProvider
{
    /// <summary>
    /// Returns a freshly generated 256-bit (32-byte) Data Encryption Key (DEK). Each call
    /// returns a unique random key — callers MUST NOT cache or reuse the result across files.
    /// </summary>
    byte[] GenerateDataKey();

    /// <summary>
    /// Wraps the supplied DEK with the KEK and returns the ciphertext envelope. The layout
    /// is opaque to callers — they pass the returned bytes verbatim to <see cref="DecryptDek"/>.
    /// </summary>
    /// <exception cref="ArgumentException">DEK length is not 32 bytes.</exception>
    byte[] EncryptDek(byte[] dek);

    /// <summary>
    /// Unwraps a previously-wrapped DEK envelope. Throws if the envelope has been tampered
    /// with (AEAD authentication failure) or if the KEK has changed since the envelope was
    /// produced (rotation has not been implemented yet, so in v0.1 this means the KEK is wrong).
    /// </summary>
    /// <exception cref="System.Security.Cryptography.AuthenticationTagMismatchException">
    /// Envelope integrity failed — either the wrapped DEK was altered or the KEK is wrong.
    /// </exception>
    byte[] DecryptDek(byte[] encryptedDek);
}
