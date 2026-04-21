using System.Security.Cryptography;
using Strg.Core.Storage;

namespace Strg.Infrastructure.Storage.Encryption;

/// <summary>
/// <see cref="IEncryptingFileWriter"/> backed by AES-256-GCM with chunked streaming. The
/// plaintext is encrypted 64 KiB at a time so peak memory stays bounded regardless of file size —
/// the ~256 MiB cap that the single-shot prototype needed is gone.
///
/// <para><b>Envelope format.</b>
/// <code>
/// magic(8)="STRGENC1" || file_nonce(12) || [chunk_ciphertext(0..64KiB) || chunk_tag(16)]*
/// </code>
/// Every file ends with an explicit final chunk — empty files produce one 16-byte tag-only chunk,
/// so the reader can distinguish "legitimate zero-byte file" from "truncated after header".</para>
///
/// <para><b>Per-chunk nonce derivation (NIST SP 800-38D §8.2.1).</b>
/// <code>
/// per_chunk_nonce = file_nonce[0..4] || LE64(chunk_index)
/// </code>
/// The 4-byte salt from file_nonce provides cross-file uniqueness (random per file); the 8-byte
/// chunk-index suffix provides in-file uniqueness. Combined with a fresh DEK per file, nonce
/// reuse is statistically impossible.</para>
///
/// <para><b>Per-chunk AAD.</b>
/// <code>
/// aad = file_nonce(12) || LE64(chunk_index) || is_final(1 byte)
/// </code>
/// Binds each chunk to its position, the end-of-stream marker, AND the envelope's file_nonce.
/// Including the full file_nonce authenticates bytes 4..11 of it — the per-chunk nonce only uses
/// bytes 0..3 as a salt, so without AAD coverage those trailing bytes would be silently tamperable.
/// Without the is_final bit a truncation attack (drop the last chunk) wouldn't be detectable —
/// the second-to-last chunk would still authenticate cleanly.</para>
///
/// <para><b>Memory hygiene.</b> The DEK is wiped after wrapping (write path) or transferred to
/// the read-side stream (read path) which wipes on Dispose. Plaintext and ciphertext buffers
/// inside the streams are zeroed when the stream is disposed.</para>
/// </summary>
public sealed class AesGcmFileWriter(IStorageProvider inner, IKeyProvider keyProvider) : IEncryptingFileWriter
{
    public const string AlgorithmName = "AES-256-GCM";

    internal const int MagicLength = 8;
    internal const int FileNonceLength = 12;
    internal const int HeaderLength = MagicLength + FileNonceLength;
    internal const int ChunkPlaintextSize = 64 * 1024;
    internal const int TagLength = 16;
    internal const int NonceSaltLength = 4;
    internal const int AadLength = FileNonceLength + 8 + 1; // file_nonce || LE64(chunk_index) || is_final

    internal static ReadOnlySpan<byte> Magic => "STRGENC1"u8;

    public async Task<EncryptedWriteResult> WriteAsync(string storageKey, Stream content, string algorithm, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storageKey);
        ArgumentNullException.ThrowIfNull(content);
        EnsureAlgorithmSupported(algorithm);

        var dek = keyProvider.GenerateDataKey();
        try
        {
            var fileNonce = new byte[FileNonceLength];
            RandomNumberGenerator.Fill(fileNonce);

            long plaintextBytes;
            await using (var encryptStream = new ChunkedGcmEncryptStream(content, dek, fileNonce))
            {
                await inner.WriteAsync(storageKey, encryptStream, cancellationToken).ConfigureAwait(false);
                plaintextBytes = encryptStream.PlaintextLength;
            }

            var wrappedDek = keyProvider.EncryptDek(dek);
            return new EncryptedWriteResult(wrappedDek, AlgorithmName, plaintextBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public async Task<Stream> ReadAsync(string storageKey, byte[] wrappedDek, string algorithm, long offset = 0, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storageKey);
        ArgumentNullException.ThrowIfNull(wrappedDek);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        EnsureAlgorithmSupported(algorithm);

        var dek = keyProvider.DecryptDek(wrappedDek);
        Stream? ciphertext = null;
        ChunkedGcmDecryptStream? decryptStream = null;
        var ownershipTransferred = false;

        try
        {
            // Must read from 0 — the envelope header + per-chunk tags only validate on a full
            // sequential replay. Partial reads bypass the tag chain (silent corruption instead of
            // explicit fail).
            ciphertext = await inner.ReadAsync(storageKey, offset: 0, cancellationToken).ConfigureAwait(false);

            var header = new byte[HeaderLength];
            var headerRead = await FillAsync(ciphertext, header, cancellationToken).ConfigureAwait(false);
            if (headerRead < HeaderLength)
            {
                throw new InvalidDataException(
                    $"Encrypted envelope at '{storageKey}' is {headerRead} bytes, shorter than the {HeaderLength}-byte header. Corruption or truncation.");
            }

            if (!header.AsSpan(0, MagicLength).SequenceEqual(Magic))
            {
                throw new InvalidDataException(
                    $"Encrypted envelope at '{storageKey}' has wrong magic bytes. Not a strg-encrypted file or corruption.");
            }

            var fileNonce = header.AsSpan(MagicLength, FileNonceLength).ToArray();

            decryptStream = new ChunkedGcmDecryptStream(ciphertext, dek, fileNonce);
            // Skip-read to offset happens here so callers that pass offset>0 get a stream whose
            // first ReadAsync already returns plaintext at the requested position. Skipped chunks
            // are still authenticated on the way through — skipping whole chunks without verifying
            // would open a tamper-detection bypass.
            await SkipBytesAsync(decryptStream, offset, cancellationToken).ConfigureAwait(false);
            ownershipTransferred = true;
            return decryptStream;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                // decryptStream's DisposeAsync already zeros the DEK and disposes `ciphertext`,
                // so either dispose that OR clean up the pieces we created so far — never both.
                if (decryptStream is not null)
                {
                    await decryptStream.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    if (ciphertext is not null)
                    {
                        await ciphertext.DisposeAsync().ConfigureAwait(false);
                    }
                    CryptographicOperations.ZeroMemory(dek);
                }
            }
        }
    }

    // Rejects unknown algorithms before any key material is touched. Throws NotSupportedException
    // rather than ArgumentException because the v0.2 dispatcher will turn this branch into a
    // "route to a different IEncryptingFileWriter" — the caller's algorithm pick is not invalid,
    // it's just not ours. NotSupportedException is the signal the dispatcher keys off.
    private static void EnsureAlgorithmSupported(string algorithm)
    {
        ArgumentException.ThrowIfNullOrEmpty(algorithm);
        if (!string.Equals(algorithm, AlgorithmName, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Algorithm '{algorithm}' is not supported by {nameof(AesGcmFileWriter)}; expected '{AlgorithmName}'.");
        }
    }

    private static async Task<int> FillAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }

    private static async Task SkipBytesAsync(Stream source, long byteCount, CancellationToken cancellationToken)
    {
        if (byteCount <= 0)
        {
            return;
        }

        var scratch = new byte[Math.Min(byteCount, 8192)];
        try
        {
            var remaining = byteCount;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(remaining, scratch.Length);
                var read = await source.ReadAsync(scratch.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    // Offset past end — return a stream that will read zero on the next call.
                    // HTTP Range 416 semantics are enforced one layer up.
                    break;
                }
                remaining -= read;
            }
        }
        finally
        {
            // Scratch holds decrypted plaintext from the skipped prefix; don't leave it on the heap.
            CryptographicOperations.ZeroMemory(scratch);
        }
    }
}
