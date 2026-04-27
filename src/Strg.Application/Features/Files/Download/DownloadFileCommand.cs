using Mediator;
using Strg.Application.Abstractions;
using Strg.Core;

namespace Strg.Application.Features.Files.Download;

/// <summary>
/// Streams a file's content for download (STRG-037). The handler resolves the drive, file,
/// and storage stream — encryption-aware — and returns an opened stream wrapped in
/// <see cref="FileDownloadResult"/>. The endpoint disposes the result after copying bytes.
///
/// <para>Marker rationale: <see cref="ITenantScopedCommand"/> rejects calls without a tenant
/// (the global filter would mask cross-tenant access as null, but the marker fails fast on a
/// missing JWT). <see cref="IAuditedCommand"/> wires this into <c>AuditBehavior</c> — audit
/// fires post-handler ONLY when <see cref="Result{T, TError}.IsSuccess"/> is true, so 404 /
/// 416 / 500 failures never produce an audit row (no bytes flowed, no access).</para>
///
/// <para><b>Typed failure payload.</b> The handler returns
/// <see cref="Result{T, TError}"/> where <c>TError</c> is the
/// <see cref="DownloadFailure"/> discriminated union. Each failure case carries its own
/// data — <see cref="DownloadFailure.RangeNotSatisfiable.Size"/> is a typed
/// <see langword="long"/> the endpoint reads directly into <c>Content-Range: bytes */{Size}</c>
/// without a free-text round-trip. The string-coded <see cref="Result{T}"/> remains the
/// default for handlers whose failures are flat.</para>
/// </summary>
public sealed record DownloadFileCommand(
    Guid DriveId,
    Guid FileId,
    DownloadRange? Range)
    : ICommand<Result<FileDownloadResult, DownloadFailure>>, ITenantScopedCommand, IAuditedCommand;

/// <summary>
/// Client-supplied byte range, mirroring the shape of <c>RangeHeaderValue.Ranges.Single()</c>.
/// The endpoint parses the raw HTTP header into this record so the application layer stays
/// HTTP-agnostic.
///
/// <list type="bullet">
///   <item><description><see cref="From"/>=K, <see cref="To"/>=L → bounded range bytes K-L</description></item>
///   <item><description><see cref="From"/>=K, <see cref="To"/>=null → open-ended bytes K to end-of-file</description></item>
///   <item><description><see cref="From"/>=null, <see cref="To"/>=N → suffix range, last N bytes</description></item>
/// </list>
/// </summary>
public sealed record DownloadRange(long? From, long? To);
