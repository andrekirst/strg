using System.Text.Json;
using Mediator;
using Strg.Application.Auditing;
using Strg.Core;
using Strg.Core.Auditing;

namespace Strg.Application.Features.Files.Download;

/// <summary>
/// Thin facade for the download command — delegates resolution to
/// <see cref="IFileDownloadResolver"/> and records the audit row on success. Audit emission
/// stays here (not in the resolver) so the resolver remains pure data-orchestration and the
/// success/failure → audit policy lives in one place.
///
/// <para><c>AuditBehavior</c> persists the recorded entry post-handler ONLY when
/// <see cref="Result{T, TError}.IsSuccess"/> is true; the failure paths return without
/// touching the audit scope, so 404 / 416 / 500 responses produce no audit row.</para>
/// </summary>
internal sealed class DownloadFileHandler(
    IFileDownloadResolver resolver,
    IAuditScope auditScope)
    : ICommandHandler<DownloadFileCommand, Result<FileDownloadResult, DownloadFailure>>
{
    public async ValueTask<Result<FileDownloadResult, DownloadFailure>> Handle(
        DownloadFileCommand command,
        CancellationToken cancellationToken)
    {
        var result = await resolver.ResolveAsync(
            command.DriveId,
            command.FileId,
            command.Range,
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            auditScope.Record(
                AuditActions.FileDownloaded,
                AuditResourceTypes.FileItem,
                result.Value!.FileId,
                BuildAuditDetails(result.Value!));
        }

        return result;
    }

    private static string BuildAuditDetails(FileDownloadResult download)
        => JsonSerializer.Serialize(new
        {
            driveId = download.DriveId,
            size = download.Size,
            range = download.IsPartial ? $"{download.PartialStart}-{download.PartialEnd}" : null,
        });
}
