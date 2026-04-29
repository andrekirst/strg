using Mediator;
using Strg.Application.Abstractions;
using Strg.Application.Features.Files.Download;
using Strg.Core;

namespace Strg.Application.Features.Files.DownloadVersion;

/// <summary>
/// STRG-044 — streams a specific historical <see cref="Strg.Core.Domain.FileVersion"/>'s
/// content. The handler resolves the drive, file, version, and storage stream — encryption-
/// aware — and returns the opened stream wrapped in <see cref="FileDownloadResult"/>. The
/// endpoint disposes the result after copying bytes.
///
/// <para>The typed-failure shape, range model, and result wrapper are deliberately reused
/// from STRG-037 (<see cref="DownloadFileCommand"/>): the failure-to-HTTP mapping in the
/// endpoint, the <c>Content-Range</c> emission on 416, and the <c>await using</c> stream-
/// disposal contract are identical concerns. <see cref="DownloadFailure.IsDirectory"/> is
/// part of the union but is unreachable for this command — directories have no versions, so
/// the resolver hits <see cref="DownloadFailure.NotFound"/> first.</para>
///
/// <para>Marker rationale: <see cref="ITenantScopedCommand"/> rejects calls without a tenant.
/// <see cref="IAuditedCommand"/> wires this into <c>AuditBehavior</c> — audit fires post-handler
/// ONLY when <see cref="Result{T, TError}.IsSuccess"/> is true, so 404 / 416 / 500 failures
/// never produce an audit row.</para>
/// </summary>
public sealed record DownloadFileVersionCommand(
    Guid DriveId,
    Guid FileId,
    int VersionNumber,
    DownloadRange? Range)
    : ICommand<Result<FileDownloadResult, DownloadFailure>>, ITenantScopedCommand, IAuditedCommand;
