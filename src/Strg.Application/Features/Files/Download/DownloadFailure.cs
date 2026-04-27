namespace Strg.Application.Features.Files.Download;

/// <summary>
/// Failure modes returned by the download flow as the <c>TError</c> of
/// <see cref="Strg.Core.Result{T, TError}"/>. Each sealed sub-record carries the data the
/// endpoint needs to compose its HTTP response — e.g.,
/// <see cref="RangeNotSatisfiable"/> carries the file <c>Size</c> as a typed
/// <see langword="long"/> so the endpoint can emit
/// <c>Content-Range: bytes */{size}</c> per RFC 7233 §4.4 without parsing it back out of
/// a free-text error message.
///
/// <para><b>Audit pipeline.</b> <c>AuditBehavior</c> only emits audit rows when the
/// outer <see cref="Strg.Core.Result{T, TError}.IsSuccess"/> is true; every case below is
/// a failure, so none of these paths produce an audit row.</para>
/// </summary>
public abstract record DownloadFailure
{
    /// <summary>Drive, file, file-on-different-drive, or unfinalized-storage-key — all collapse to 404.</summary>
    public sealed record NotFound(string Detail) : DownloadFailure;

    /// <summary>FileItem.IsDirectory is true. 400.</summary>
    public sealed record IsDirectory(string Detail) : DownloadFailure;

    /// <summary>
    /// The requested byte range cannot be satisfied. <see cref="Size"/> is the total file
    /// size in bytes — the endpoint passes it straight into <c>Content-Range: bytes */{Size}</c>.
    /// </summary>
    public sealed record RangeNotSatisfiable(long Size) : DownloadFailure;

    /// <summary>
    /// Server invariant violation — a FileVersion or FileKey row required to decrypt the
    /// file is missing, or VersionCount is non-positive. <see cref="Detail"/> carries the
    /// internal identifier for log correlation; never any key material. 500.
    /// </summary>
    public sealed record InternalState(string Detail) : DownloadFailure;
}
