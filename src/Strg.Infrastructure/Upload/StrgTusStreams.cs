using System.Security.Cryptography;

namespace Strg.Infrastructure.Upload;

/// <summary>
/// Read-only stream wrapper that counts bytes read on the way through. Used by
/// <see cref="StrgTusStore.AppendDataAsync"/> to know how many bytes the chunk write actually
/// consumed from the request body — <see cref="Strg.Core.Storage.IStorageProvider.AppendAsync"/>
/// does not return a count, and the input stream's position is not always reliable for
/// chunked-transfer-encoded request bodies.
/// </summary>
internal sealed class CountingReadStream(Stream inner) : Stream
{
    public long BytesRead { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (read > 0)
        {
            BytesRead += read;
        }
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            BytesRead += read;
        }
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            BytesRead += read;
        }
        return read;
    }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Read-only stream wrapper that computes a SHA-256 hash incrementally over the bytes that flow
/// through it. The TUS finalize step pipes the assembled raw plaintext through this wrapper into
/// <see cref="Strg.Core.Storage.IEncryptingFileWriter.WriteAsync"/> so the
/// <see cref="Strg.Core.Domain.FileVersion.ContentHash"/> reflects the plaintext (NOT the
/// envelope) without a second pass over the bytes.
///
/// <para>Call <see cref="GetHashHex"/> exactly once after the stream has been fully consumed.
/// Calling before EOF returns the partial hash; calling twice resets the state.</para>
/// </summary>
internal sealed class Sha256ReadStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private string? _finalHex;

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (read > 0)
        {
            _hash.AppendData(buffer.AsSpan(offset, read));
        }
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _hash.AppendData(buffer.Span[..read]);
        }
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _hash.AppendData(buffer.AsSpan(offset, read));
        }
        return read;
    }

    public string GetHashHex()
    {
        if (_finalHex is not null)
        {
            return _finalHex;
        }
        var bytes = _hash.GetHashAndReset();
        _finalHex = Convert.ToHexStringLower(bytes);
        return _finalHex;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
