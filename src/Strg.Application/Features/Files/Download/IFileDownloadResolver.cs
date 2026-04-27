using Strg.Core;

namespace Strg.Application.Features.Files.Download;

/// <summary>
/// Resolves a download request to either an open <see cref="FileDownloadResult"/> or a typed
/// <see cref="DownloadFailure"/>. Owns the drive lookup, file lookup, range satisfiability,
/// and encryption-aware stream open. Does NOT touch the audit pipeline — the calling handler
/// records the audit on success so the audit-vs-data-orchestration concerns stay separate.
///
/// <para><b>Tenant scoping</b> is enforced transparently by the repositories' EF global query
/// filters; cross-tenant ids return null and collapse to <see cref="DownloadFailure.NotFound"/>.</para>
/// </summary>
internal interface IFileDownloadResolver
{
    Task<Result<FileDownloadResult, DownloadFailure>> ResolveAsync(
        Guid driveId,
        Guid fileId,
        DownloadRange? range,
        CancellationToken cancellationToken);
}
