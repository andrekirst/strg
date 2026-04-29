using Strg.Application.Features.Files.Download;
using Strg.Core;

namespace Strg.Application.Features.Files.DownloadVersion;

/// <summary>
/// Resolves a version-download request to either an open <see cref="FileDownloadResult"/> or a
/// typed <see cref="DownloadFailure"/>. Sibling of <c>IFileDownloadResolver</c> — owns drive
/// lookup, file lookup, version lookup, range satisfiability, and encryption-aware stream
/// open. Does NOT touch the audit pipeline; the calling handler records the audit on success
/// so audit-vs-data-orchestration concerns stay separate.
///
/// <para><b>Tenant scoping</b> is enforced transparently by the file repository's EF global
/// query filter; cross-tenant ids return null and collapse to <see cref="DownloadFailure.NotFound"/>.
/// <see cref="Strg.Core.Domain.FileVersion"/> is NOT tenanted directly — reaching it through the
/// tenant-filtered <see cref="Strg.Core.Domain.FileItem"/> is the canonical safety chain.</para>
/// </summary>
internal interface IFileVersionDownloadResolver
{
    Task<Result<FileDownloadResult, DownloadFailure>> ResolveAsync(
        Guid driveId,
        Guid fileId,
        int versionNumber,
        DownloadRange? range,
        CancellationToken cancellationToken);
}
