using System.Text.Json;
using Mediator;
using Strg.Application.Auditing;
using Strg.Application.Features.Files.Download;
using Strg.Core;
using Strg.Core.Auditing;

namespace Strg.Application.Features.Files.DownloadVersion;

/// <summary>
/// STRG-044 — thin facade over <see cref="IFileVersionDownloadResolver"/>. Records the
/// <see cref="AuditActions.FileVersionDownloaded"/> audit row on success; failure paths return
/// without touching the audit scope, so 404 / 416 / 500 responses produce no audit row.
///
/// <para><c>AuditBehavior</c> persists the recorded entry post-handler ONLY when
/// <see cref="Result{T, TError}.IsSuccess"/> is true.</para>
/// </summary>
internal sealed class DownloadFileVersionHandler(
    IFileVersionDownloadResolver resolver,
    IAuditScope auditScope)
    : ICommandHandler<DownloadFileVersionCommand, Result<FileDownloadResult, DownloadFailure>>
{
    public async ValueTask<Result<FileDownloadResult, DownloadFailure>> Handle(
        DownloadFileVersionCommand command,
        CancellationToken cancellationToken)
    {
        var result = await resolver.ResolveAsync(
            command.DriveId,
            command.FileId,
            command.VersionNumber,
            command.Range,
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            auditScope.Record(
                AuditActions.FileVersionDownloaded,
                AuditResourceTypes.FileItem,
                result.Value!.FileId,
                BuildAuditDetails(result.Value!, command.VersionNumber));
        }

        return result;
    }

    private static string BuildAuditDetails(FileDownloadResult download, int versionNumber)
        => JsonSerializer.Serialize(new
        {
            driveId = download.DriveId,
            versionNumber,
            size = download.Size,
            range = download.IsPartial ? $"{download.PartialStart}-{download.PartialEnd}" : null,
        });
}
