namespace Strg.Application.Features.Files.Download;

/// <summary>
/// Successful resolution of a <see cref="DownloadFileCommand"/>. Carries the opened source
/// stream alongside the headers the endpoint must emit. Implements
/// <see cref="IAsyncDisposable"/> so the endpoint's <c>await using</c> guarantees the stream
/// is disposed (which, for the encrypted path, also zeros the DEK held by
/// <c>ChunkedGcmDecryptStream</c>).
///
/// <para><b>Stream ownership.</b> The handler opens the stream and transfers ownership to
/// this record. The caller (endpoint) MUST dispose this result exactly once. Disposal of the
/// result disposes the contained stream — never dispose the stream directly while the result
/// is still wrapped, and never dispose the result twice.</para>
/// </summary>
public sealed class FileDownloadResult : IAsyncDisposable
{
    public required Stream Content { get; init; }

    /// <summary>Plaintext byte count of the underlying file (NOT the partial-response length).</summary>
    public required long Size { get; init; }

    public required string MimeType { get; init; }

    public required string Filename { get; init; }

    /// <summary>Identifies the resolved <c>FileItem</c> — used by the handler facade to compose the audit row.</summary>
    public required Guid FileId { get; init; }

    /// <summary>Identifies the owning <c>Drive</c> — included in the audit row's details JSON.</summary>
    public required Guid DriveId { get; init; }

    /// <summary>Inclusive start byte for a partial response, <see langword="null"/> for full file.</summary>
    public long? PartialStart { get; init; }

    /// <summary>Inclusive end byte for a partial response, <see langword="null"/> for full file.</summary>
    public long? PartialEnd { get; init; }

    /// <summary>True when the response is a single satisfied byte range (HTTP 206).</summary>
    public bool IsPartial => PartialStart.HasValue && PartialEnd.HasValue;

    /// <summary>Number of bytes the endpoint should write to the response body.</summary>
    public long ResponseLength => IsPartial ? PartialEnd!.Value - PartialStart!.Value + 1 : Size;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
