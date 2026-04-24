using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Strg.Infrastructure.Storage.Encryption;

/// <summary>
/// Read-only forward <see cref="Stream"/> that decrypts the chunk-by-chunk envelope emitted by
/// <see cref="ChunkedGcmEncryptStream"/>. Mirrors the writer's lookahead-one-chunk invariant:
/// before decrypting chunk <i>N</i>, peeks chunk <i>N+1</i>. If the peek yields 0 bytes, chunk
/// <i>N</i> is the final chunk and its AAD carries <c>is_final=1</c>.
///
/// <para><b>DEK ownership.</b> This stream OWNS the DEK and the inner ciphertext stream. The
/// writer transfers ownership after the envelope header is parsed; on <see cref="DisposeAsync"/>
/// the DEK is zeroed, the AesGcm primitive is disposed, and the inner stream is disposed. The
/// caller MUST dispose this stream even if a mid-read exception fires — otherwise the DEK leaks
/// into the managed heap for the remainder of the process.</para>
///
/// <para><b>AAD binding.</b> Any chunk reordering, duplication, truncation, or final-flag
/// flipping breaks the AAD and surfaces as <see cref="AuthenticationTagMismatchException"/>
/// from the underlying <see cref="AesGcm"/>. This is the defence against a malicious inner
/// provider returning a valid-looking-but-reordered envelope.</para>
/// </summary>
internal sealed class ChunkedGcmDecryptStream(Stream ciphertext, byte[] dek, byte[] fileNonce) : Stream
{
    private readonly AesGcm _aes = new(dek, AesGcmFileWriter.TagLength);

    private readonly byte[] _currentChunk = new byte[AesGcmFileWriter.ChunkPlaintextSize + AesGcmFileWriter.TagLength];
    private readonly byte[] _nextChunk = new byte[AesGcmFileWriter.ChunkPlaintextSize + AesGcmFileWriter.TagLength];
    private int _currentLen;

    private readonly byte[] _plaintext = new byte[AesGcmFileWriter.ChunkPlaintextSize];
    private int _plaintextOffset;
    private int _plaintextLength;

    private long _chunkIndex;
    private bool _firstIteration = true;
    private bool _eof;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        // Drain whatever is already decrypted before pulling the next chunk. A caller reading in
        // small buffers must be able to consume the 64 KiB plaintext across many ReadAsync calls.
        if (_plaintextOffset < _plaintextLength)
        {
            var available = _plaintextLength - _plaintextOffset;
            var toCopy = Math.Min(available, buffer.Length);
            _plaintext.AsMemory(_plaintextOffset, toCopy).CopyTo(buffer);
            _plaintextOffset += toCopy;
            return toCopy;
        }

        if (_eof)
        {
            return 0;
        }

        if (_firstIteration)
        {
            _currentLen = await FillAsync(ciphertext, _currentChunk, cancellationToken).ConfigureAwait(false);
            _firstIteration = false;
            if (_currentLen == 0)
            {
                // Envelope with just a valid header and no chunks isn't a legitimate encoding —
                // even the empty file produces one explicit final chunk carrying a tag.
                throw new InvalidDataException("Encrypted envelope is missing its final chunk (truncated after header).");
            }
        }

        var nextLen = await FillAsync(ciphertext, _nextChunk, cancellationToken).ConfigureAwait(false);
        var isFinal = nextLen == 0;

        if (_currentLen < AesGcmFileWriter.TagLength)
        {
            throw new InvalidDataException(
                $"Encrypted chunk is {_currentLen} bytes, below the minimum {AesGcmFileWriter.TagLength} (tag). Corruption or truncation.");
        }

        var ciphertextLen = _currentLen - AesGcmFileWriter.TagLength;
        DecryptChunk(
            _currentChunk.AsSpan(0, ciphertextLen),
            _currentChunk.AsSpan(ciphertextLen, AesGcmFileWriter.TagLength),
            _plaintext.AsSpan(0, ciphertextLen),
            _chunkIndex,
            isFinal);

        _plaintextOffset = 0;
        _plaintextLength = ciphertextLen;
        _chunkIndex++;

        if (isFinal)
        {
            _eof = true;
        }
        else
        {
            Array.Copy(_nextChunk, _currentChunk, nextLen);
            _currentLen = nextLen;
        }

        return await ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    private void DecryptChunk(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext, long chunkIndex, bool isFinal)
    {
        Span<byte> nonce = stackalloc byte[AesGcmFileWriter.FileNonceLength];
        fileNonce.AsSpan(0, AesGcmFileWriter.NonceSaltLength).CopyTo(nonce[..AesGcmFileWriter.NonceSaltLength]);
        BinaryPrimitives.WriteInt64LittleEndian(nonce[AesGcmFileWriter.NonceSaltLength..], chunkIndex);

        Span<byte> aad = stackalloc byte[AesGcmFileWriter.AadLength];
        fileNonce.AsSpan(0, AesGcmFileWriter.FileNonceLength).CopyTo(aad[..AesGcmFileWriter.FileNonceLength]);
        BinaryPrimitives.WriteInt64LittleEndian(aad.Slice(AesGcmFileWriter.FileNonceLength, 8), chunkIndex);
        aad[AesGcmFileWriter.FileNonceLength + 8] = isFinal ? (byte)1 : (byte)0;

        _aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
    }

    private static async ValueTask<int> FillAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
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

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _aes.Dispose();
            ciphertext.Dispose();
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(_plaintext);
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        _aes.Dispose();
        await ciphertext.DisposeAsync().ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(dek);
        CryptographicOperations.ZeroMemory(_plaintext);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
