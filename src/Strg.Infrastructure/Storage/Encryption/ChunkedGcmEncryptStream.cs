using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Strg.Infrastructure.Storage.Encryption;

/// <summary>
/// Read-only forward <see cref="Stream"/> that emits the encrypted envelope produced by
/// <see cref="AesGcmFileWriter"/>. Pulls plaintext from <see cref="_source"/> on demand, one
/// chunk at a time, so neither the plaintext nor the ciphertext is ever fully materialized in
/// memory — peak memory is bounded by two chunk buffers (~128 KiB).
///
/// <para><b>Lookahead-one-chunk invariant.</b> Before encrypting chunk <i>N</i>, the stream
/// peeks chunk <i>N+1</i>. If the peek yields 0 bytes, chunk <i>N</i> is the final chunk and its
/// AAD carries <c>is_final=1</c>. Without this lookahead, the encrypt code could not set the
/// AAD's final-flag correctly, and the resulting envelope would not survive truncation attacks.</para>
///
/// <para><b>Empty-file handling.</b> A zero-byte source still emits one final chunk with
/// zero-length ciphertext — AES-GCM authenticates the empty string against the AAD + nonce, so
/// the reader can distinguish "legitimate empty file" from "truncated-after-header".</para>
///
/// <para><b>DEK lifetime.</b> This stream does NOT own the DEK. The writer zeros the DEK after
/// calling <see cref="IStorageProvider.WriteAsync"/> on the inner provider — by which point this
/// stream has been fully consumed.</para>
/// </summary>
internal sealed class ChunkedGcmEncryptStream : Stream
{
    private readonly Stream _source;
    private readonly byte[] _fileNonce;
    private readonly AesGcm _aes;

    private readonly byte[] _currentPlaintext = new byte[AesGcmFileWriter.ChunkPlaintextSize];
    private readonly byte[] _nextPlaintext = new byte[AesGcmFileWriter.ChunkPlaintextSize];
    private int _currentLen;

    private byte[]? _emitBuffer;
    private int _emitOffset;
    private int _emitLength;

    private long _chunkIndex;
    private bool _firstIteration = true;
    private bool _headerEmitted;
    private bool _eof;

    private long _plaintextBytesRead;

    public ChunkedGcmEncryptStream(Stream source, byte[] dek, byte[] fileNonce)
    {
        _source = source;
        _fileNonce = fileNonce;
        _aes = new AesGcm(dek, AesGcmFileWriter.TagLength);
    }

    /// <summary>Cumulative plaintext bytes pulled from the source. Populated as the stream is consumed.</summary>
    public long PlaintextLength => _plaintextBytesRead;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        // Serve bytes already staged in the emit buffer first — a large envelope read may span
        // multiple ReadAsync calls on the consumer side.
        if (_emitBuffer is not null && _emitOffset < _emitLength)
        {
            var available = _emitLength - _emitOffset;
            var toCopy = Math.Min(available, buffer.Length);
            _emitBuffer.AsMemory(_emitOffset, toCopy).CopyTo(buffer);
            _emitOffset += toCopy;
            return toCopy;
        }

        if (!_headerEmitted)
        {
            var header = new byte[AesGcmFileWriter.HeaderLength];
            AesGcmFileWriter.Magic.CopyTo(header.AsSpan(0, AesGcmFileWriter.MagicLength));
            _fileNonce.AsSpan().CopyTo(header.AsSpan(AesGcmFileWriter.MagicLength, AesGcmFileWriter.FileNonceLength));
            StageEmit(header);
            _headerEmitted = true;
            return await ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        if (_eof)
        {
            return 0;
        }

        if (_firstIteration)
        {
            _currentLen = await FillAsync(_source, _currentPlaintext, cancellationToken).ConfigureAwait(false);
            _firstIteration = false;
        }

        var nextLen = await FillAsync(_source, _nextPlaintext, cancellationToken).ConfigureAwait(false);
        var isFinal = nextLen == 0;

        var chunkEnvelope = new byte[_currentLen + AesGcmFileWriter.TagLength];
        EncryptChunk(
            _currentPlaintext.AsSpan(0, _currentLen),
            chunkEnvelope.AsSpan(0, _currentLen),
            chunkEnvelope.AsSpan(_currentLen, AesGcmFileWriter.TagLength),
            _chunkIndex,
            isFinal);

        _plaintextBytesRead += _currentLen;
        StageEmit(chunkEnvelope);
        _chunkIndex++;

        if (isFinal)
        {
            _eof = true;
        }
        else
        {
            // Rotate: next becomes current. Array.Copy instead of buffer swap so the
            // two readonly buffer references stay stable (simpler than juggling swaps).
            Array.Copy(_nextPlaintext, _currentPlaintext, nextLen);
            _currentLen = nextLen;
        }

        return await ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    private void EncryptChunk(ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag, long chunkIndex, bool isFinal)
    {
        Span<byte> nonce = stackalloc byte[AesGcmFileWriter.FileNonceLength];
        _fileNonce.AsSpan(0, AesGcmFileWriter.NonceSaltLength).CopyTo(nonce[..AesGcmFileWriter.NonceSaltLength]);
        BinaryPrimitives.WriteInt64LittleEndian(nonce[AesGcmFileWriter.NonceSaltLength..], chunkIndex);

        Span<byte> aad = stackalloc byte[AesGcmFileWriter.AadLength];
        _fileNonce.AsSpan(0, AesGcmFileWriter.FileNonceLength).CopyTo(aad[..AesGcmFileWriter.FileNonceLength]);
        BinaryPrimitives.WriteInt64LittleEndian(aad.Slice(AesGcmFileWriter.FileNonceLength, 8), chunkIndex);
        aad[AesGcmFileWriter.FileNonceLength + 8] = isFinal ? (byte)1 : (byte)0;

        _aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
    }

    private void StageEmit(byte[] buffer)
    {
        _emitBuffer = buffer;
        _emitOffset = 0;
        _emitLength = buffer.Length;
    }

    private static async ValueTask<int> FillAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        // ReadExactlyAsync would throw if EOF mid-buffer; we want "read up to buffer.Length,
        // EOF is fine mid-read". So manual loop.
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
            // Wipe the plaintext chunk buffers — they held plaintext bytes that the GC would
            // otherwise leave on the managed heap until the next collection.
            CryptographicOperations.ZeroMemory(_currentPlaintext);
            CryptographicOperations.ZeroMemory(_nextPlaintext);
        }
        base.Dispose(disposing);
    }
}
