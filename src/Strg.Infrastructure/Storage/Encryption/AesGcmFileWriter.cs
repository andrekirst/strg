using System.Security.Cryptography;
using Strg.Core.Storage;

namespace Strg.Infrastructure.Storage.Encryption;

/// <summary>
/// <see cref="IEncryptingFileWriter"/> backed by AES-256-GCM with a random 96-bit nonce per file.
/// Ciphertext layout on the inner storage provider: <c>nonce(12) || ciphertext(plaintext.Length) || tag(16)</c>.
///
/// <para><b>v0.1 single-shot limitation.</b> <see cref="AesGcm"/> is a single-shot API — there is
/// no native chunked-streaming mode, and rolling one by hand with safe truncation resistance
/// (AAD binds chunk index + final-flag, nonce derivation avoids reuse) is complex enough that
/// shipping it unreviewed is the wrong call for v0.1. Instead, the plaintext is buffered into
/// memory and capped at <see cref="MaxEncryptedFileSizeBytes"/>. Chunked streaming for
/// arbitrary-size files is a v0.2 tracker.</para>
///
/// <para><b>Memory hygiene.</b> The DEK is wiped with <see cref="CryptographicOperations.ZeroMemory"/>
/// in the outer <c>finally</c>. The plaintext buffer is also wiped before its <see cref="MemoryStream"/>
/// is disposed — otherwise the backing array lingers in the ArrayPool / GC heap with unencrypted bytes.</para>
/// </summary>
public sealed class AesGcmFileWriter(IStorageProvider inner, IKeyProvider keyProvider) : IEncryptingFileWriter
{
    public const string AlgorithmName = "AES-256-GCM";

    private const int NonceLengthBytes = 12;
    private const int TagLengthBytes = 16;

    /// <summary>
    /// v0.1 upper bound on plaintext size for encrypted drives (256 MiB). Buffered-in-memory
    /// encryption past this would routinely blow the app's working set. Chunked streaming lands
    /// in v0.2; callers should surface this cap as a clear startup/endpoint error rather than
    /// a mysterious OOM.
    /// </summary>
    public const long MaxEncryptedFileSizeBytes = 256L * 1024 * 1024;

    public async Task<EncryptedWriteResult> WriteAsync(string storageKey, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storageKey);
        ArgumentNullException.ThrowIfNull(content);

        var dek = keyProvider.GenerateDataKey();
        try
        {
            using var plaintextBuffer = new MemoryStream();
            await content.CopyToAsync(plaintextBuffer, cancellationToken).ConfigureAwait(false);

            if (plaintextBuffer.Length > MaxEncryptedFileSizeBytes)
            {
                throw new InvalidOperationException(
                    $"File of {plaintextBuffer.Length} bytes exceeds the v0.1 encrypted-drive limit "
                    + $"of {MaxEncryptedFileSizeBytes} bytes. Chunked streaming encryption is a v0.2 tracker.");
            }

            var plaintextLength = (int)plaintextBuffer.Length;
            var envelope = new byte[NonceLengthBytes + plaintextLength + TagLengthBytes];
            var nonceSpan = envelope.AsSpan(0, NonceLengthBytes);
            var ciphertextSpan = envelope.AsSpan(NonceLengthBytes, plaintextLength);
            var tagSpan = envelope.AsSpan(NonceLengthBytes + plaintextLength, TagLengthBytes);

            RandomNumberGenerator.Fill(nonceSpan);

            using (var aes = new AesGcm(dek, TagLengthBytes))
            {
                aes.Encrypt(nonceSpan, plaintextBuffer.GetBuffer().AsSpan(0, plaintextLength), ciphertextSpan, tagSpan);
            }

            // Wipe the plaintext buffer before the MemoryStream's backing array escapes into
            // the managed heap. GetBuffer() may return an over-allocated array; zeroing the full
            // capacity is correct.
            CryptographicOperations.ZeroMemory(plaintextBuffer.GetBuffer());

            using (var envelopeStream = new MemoryStream(envelope, writable: false))
            {
                await inner.WriteAsync(storageKey, envelopeStream, cancellationToken).ConfigureAwait(false);
            }

            var wrappedDek = keyProvider.EncryptDek(dek);
            return new EncryptedWriteResult(wrappedDek, AlgorithmName, plaintextLength);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public async Task<Stream> ReadAsync(string storageKey, byte[] wrappedDek, long offset = 0, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storageKey);
        ArgumentNullException.ThrowIfNull(wrappedDek);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        var dek = keyProvider.DecryptDek(wrappedDek);
        try
        {
            byte[] envelope;
            int envelopeLength;
            await using (var ciphertextStream = await inner.ReadAsync(storageKey, offset: 0, cancellationToken).ConfigureAwait(false))
            using (var envelopeBuffer = new MemoryStream())
            {
                // Must read from offset 0 — GCM authentication covers the full envelope, and a
                // partial read bypasses the tag check (silent corruption instead of explicit fail).
                await ciphertextStream.CopyToAsync(envelopeBuffer, cancellationToken).ConfigureAwait(false);
                envelopeLength = (int)envelopeBuffer.Length;
                envelope = envelopeBuffer.ToArray();
            }

            if (envelopeLength < NonceLengthBytes + TagLengthBytes)
            {
                throw new InvalidDataException(
                    $"Ciphertext envelope at '{storageKey}' is {envelopeLength} bytes, shorter than the "
                    + $"minimum {NonceLengthBytes + TagLengthBytes} bytes (nonce + tag). Corruption or truncation.");
            }

            var ciphertextLength = envelopeLength - NonceLengthBytes - TagLengthBytes;
            var nonce = envelope.AsSpan(0, NonceLengthBytes);
            var ciphertext = envelope.AsSpan(NonceLengthBytes, ciphertextLength);
            var tag = envelope.AsSpan(NonceLengthBytes + ciphertextLength, TagLengthBytes);

            var plaintext = new byte[ciphertextLength];
            using (var aes = new AesGcm(dek, TagLengthBytes))
            {
                // AuthenticationTagMismatchException surfaces to the caller for tampering / wrong-KEK.
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
            }

            if (offset >= plaintext.Length)
            {
                // Offset past end → empty stream. HTTP Range semantics (416) are enforced one
                // layer up; this layer just reports "nothing left".
                return new MemoryStream(Array.Empty<byte>(), writable: false);
            }

            return new MemoryStream(plaintext, (int)offset, plaintext.Length - (int)offset, writable: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }
}
