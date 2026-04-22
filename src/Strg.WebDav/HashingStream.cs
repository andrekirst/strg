using System.Security.Cryptography;

namespace Strg.WebDav;

/// <summary>
/// STRG-070 — read-side stream wrapper that feeds every byte it yields through an
/// <see cref="IncrementalHash"/> while tracking the total byte count. Designed for the WebDAV PUT
/// path: the HTTP request body is the source, <see cref="Core.Storage.IStorageProvider.WriteAsync"/>
/// is the sink, and the hash/bytes fall out as a side-effect of the <c>CopyToAsync</c> pump.
///
/// <para><b>Why incremental, not a post-write pass.</b> A second read pass to compute SHA-256
/// would require the blob to be re-opened from storage — fine on SSD, prohibitive for multi-GB
/// WebDAV uploads where the storage provider may be remote (S3, encrypted chunked GCM). Hashing
/// in-flight keeps the PUT handler single-pass and storage-provider-agnostic.</para>
///
/// <para><b>Disposal finalizes the hash.</b> The hash is read out via <see cref="GetHashAndReset"/>
/// (idempotent — the same bytes produce the same digest) rather than captured at dispose, so a
/// caller that forgets to dispose still gets the digest. The contained <see cref="IncrementalHash"/>
/// is disposed alongside this stream; callers should not reuse the hasher afterwards.</para>
///
/// <para><b>Read-only.</b> This stream exposes the read surface only — writes throw. The intent
/// is a one-way pipe: provider reads from us, we read from inner, we hash in passing.</para>
/// </summary>
internal sealed class HashingStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _hasher;
    private readonly bool _leaveInnerOpen;
    private long _bytesRead;
    private bool _disposed;

    public HashingStream(Stream inner, IncrementalHash hasher, bool leaveInnerOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(hasher);
        if (!inner.CanRead)
        {
            throw new ArgumentException("Inner stream must be readable.", nameof(inner));
        }

        _inner = inner;
        _hasher = hasher;
        _leaveInnerOpen = leaveInnerOpen;
    }

    /// <summary>Total bytes pulled from the inner stream since construction.</summary>
    public long BytesRead => _bytesRead;

    public override bool CanRead => !_disposed && _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => _bytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0)
        {
            _hasher.AppendData(buffer, offset, read);
            _bytesRead += read;
        }
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        if (read > 0)
        {
            _hasher.AppendData(buffer[..read]);
            _bytesRead += read;
        }
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _hasher.AppendData(buffer.Span[..read]);
            _bytesRead += read;
        }
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>
    /// Finalizes the running hash and returns it. Safe to call once total; subsequent calls return
    /// the empty-state hash because <see cref="IncrementalHash.GetHashAndReset"/> clears the buffer
    /// — WebDAV PUT only needs the digest once (post-write) so this is intentional.
    /// </summary>
    public byte[] GetHashAndReset() => _hasher.GetHashAndReset();

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _hasher.Dispose();
            if (!_leaveInnerOpen)
            {
                _inner.Dispose();
            }
        }
        _disposed = true;
        base.Dispose(disposing);
    }
}
