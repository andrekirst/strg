using Mediator;
using Strg.Core.Domain;

namespace Strg.Application.Features.Files.ListVersions;

/// <summary>
/// Handles <see cref="ListFileVersionsQuery"/>. Resolves the file through
/// <see cref="IFileRepository.GetByIdAsync"/> first so the global tenant + soft-delete filters
/// gate access — <see cref="FileVersion"/> does NOT inherit <c>TenantedEntity</c>, so reaching
/// versions via a tenant-filtered file is the only safe path. Cross-drive id mismatch and
/// directory-targeting both collapse to <see langword="null"/> (404), matching the established
/// convention in <c>FileDownloadResolver</c> and <c>DeleteFileHandler</c>.
/// </summary>
internal sealed class ListFileVersionsHandler(
    IFileRepository fileRepository,
    IFileVersionRepository versionRepository)
    : IQueryHandler<ListFileVersionsQuery, IReadOnlyList<FileVersionView>?>
{
    public async ValueTask<IReadOnlyList<FileVersionView>?> Handle(
        ListFileVersionsQuery query,
        CancellationToken cancellationToken)
    {
        var file = await fileRepository.GetByIdAsync(query.FileId, cancellationToken).ConfigureAwait(false);
        if (file is null || file.DriveId != query.DriveId || file.IsDirectory)
        {
            return null;
        }

        var versions = await versionRepository.ListAsync(file.Id, cancellationToken).ConfigureAwait(false);

        var projection = new List<FileVersionView>(versions.Count);
        foreach (var version in versions)
        {
            projection.Add(new FileVersionView(
                version.VersionNumber,
                version.Size,
                version.ContentHash,
                version.CreatedAt,
                version.CreatedBy));
        }

        return projection;
    }
}
