using Microsoft.AspNetCore.Mvc;
using Strg.Api.Auth;
using Strg.Core.Domain;
using Strg.Core.Services;
using Strg.Core.Storage;

namespace Strg.Api.Endpoints;

/// <summary>
/// STRG-044 — REST endpoints exposing per-file version history. Two routes:
/// <list type="bullet">
/// <item><description><c>GET .../files/{fileId}/versions</c> — projects every
/// <see cref="FileVersion"/> for the file into <see cref="FileVersionDto"/>, ordered DESCENDING
/// by <see cref="FileVersion.VersionNumber"/> (newest first).</description></item>
/// <item><description><c>GET .../files/{fileId}/versions/{versionNumber}/content</c> — streams the
/// blob for a specific version with HTTP Range support (206 Partial Content via
/// <c>Results.File(..., enableRangeProcessing: true)</c>).</description></item>
/// </list>
///
/// <para><b>Tenant isolation.</b> The list endpoint resolves the owning <see cref="FileItem"/>
/// through the tenant-filtered <see cref="IFileRepository.GetByIdAsync"/> BEFORE calling
/// <see cref="IFileVersionStore.GetVersionsAsync"/>. <see cref="FileVersion"/> inherits
/// <see cref="Strg.Core.Domain.Entity"/> rather than <see cref="TenantedEntity"/>, so the global
/// query filter does not cover it directly — the file lookup is the tenant gate. The content
/// endpoint reuses <see cref="IFileVersionStore.GetVersionAsync"/> which performs the same
/// file-then-version lookup; cross-tenant probes collapse to 404.</para>
///
/// <para><b>Authorization.</b> Both routes require the <c>files.read</c> scope via
/// <see cref="AuthPolicies.FilesRead"/>. Scope-deficient callers get HTTP 403 before the handler
/// runs.</para>
///
/// <para><b>What this endpoint does NOT expose.</b> <see cref="FileVersion.StorageKey"/> is the
/// provider-internal addressing token; leaking it would let a caller bypass the auth/range
/// pipeline by deriving a direct provider URL. The <see cref="FileVersionDto"/> projection drops
/// it explicitly.</para>
///
/// <para><b>Encryption note.</b> The content endpoint streams whatever bytes the storage
/// provider returns at <see cref="FileVersion.StorageKey"/>. For encrypted drives those bytes
/// are the AES-GCM envelope, not the plaintext — this matches the STRG-044 issue spec's
/// handler shape (direct <c>provider.ReadAsync</c>) and is consistent with the parallel
/// STRG-045 restore path. Per-version download of plaintext on encrypted drives requires the
/// <see cref="IEncryptingFileWriter"/> dispatch the current-version download
/// (<c>FileDownloadResolver</c>) performs; that is out of scope for STRG-044 and tracked as a
/// follow-up.</para>
/// </summary>
public static class FileVersionEndpoints
{
    public static IEndpointRouteBuilder MapFileVersionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/v1/drives/{driveId:guid}/files/{fileId:guid}/versions",
                ListVersionsAsync)
            .RequireAuthorization(AuthPolicies.FilesRead)
            .WithName("ListFileVersions")
            .WithTags("Files")
            .WithSummary("List every version of a file, newest first.")
            .WithDescription(
                "Returns the full version history of the target file ordered DESCENDING by " +
                "versionNumber (latest first). The provider-internal storage key is excluded " +
                "from the response. Returns 404 if the file does not exist or belongs to a " +
                "different drive than the route specifies.");

        app.MapGet(
                "/api/v1/drives/{driveId:guid}/files/{fileId:guid}/versions/{versionNumber:int}/content",
                GetVersionContentAsync)
            .RequireAuthorization(AuthPolicies.FilesRead)
            .WithName("GetFileVersionContent")
            .WithTags("Files")
            .WithSummary("Stream the blob for a specific version of a file.")
            .WithDescription(
                "Streams the content of the requested version. Supports HTTP Range " +
                "(206 Partial Content) via standard ASP.NET Core file-result range processing. " +
                "Returns 404 if the file or the requested versionNumber does not exist, or if " +
                "the file belongs to a different drive than the route specifies.");

        return app;
    }

    private static async Task<IResult> ListVersionsAsync(
        Guid driveId,
        Guid fileId,
        [FromServices] IFileRepository fileRepository,
        [FromServices] IFileVersionStore versionStore,
        CancellationToken cancellationToken)
    {
        // Tenant gate. IFileVersionStore.GetVersionsAsync issues an un-tenanted query against the
        // FileVersion table (FileVersion inherits Entity, not TenantedEntity). Without the prior
        // tenant-filtered FileItem lookup, a caller in tenant A could enumerate version history of
        // a file in tenant B by guessing fileId values.
        var file = await fileRepository.GetByIdAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (file is null || file.DriveId != driveId)
        {
            // Drive mismatch is also collapsed to 404 — preventing the capability-confusion shape
            // where a caller addresses a known fileId via an unrelated drive id they happen to
            // have read access on.
            return Results.NotFound();
        }

        var versions = await versionStore.GetVersionsAsync(fileId, cancellationToken).ConfigureAwait(false);

        // GetVersionsAsync already returns newest-first (per contract on IFileVersionStore +
        // FileVersionRepository.ListAsync). The endpoint trusts that contract and projects
        // 1-to-1 — re-sorting here would mask a future regression in the store.
        var dtos = versions
            .Select(v => new FileVersionDto(
                v.VersionNumber,
                v.Size,
                v.ContentHash,
                v.CreatedAt,
                v.CreatedBy))
            .ToArray();

        return Results.Ok(dtos);
    }

    private static async Task<IResult> GetVersionContentAsync(
        Guid driveId,
        Guid fileId,
        int versionNumber,
        [FromServices] IFileRepository fileRepository,
        [FromServices] IDriveRepository driveRepository,
        [FromServices] IFileVersionStore versionStore,
        [FromServices] IStorageProviderRegistry providerRegistry,
        CancellationToken cancellationToken)
    {
        var file = await fileRepository.GetByIdAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (file is null || file.DriveId != driveId)
        {
            return Results.NotFound();
        }

        // GetVersionAsync re-checks the file via fileRepo internally (its own tenant gate); the
        // outer fileRepo lookup above is the cross-drive guard, which GetVersionAsync does not
        // know about. Both are load-bearing.
        var version = await versionStore
            .GetVersionAsync(fileId, versionNumber, cancellationToken)
            .ConfigureAwait(false);
        if (version is null)
        {
            return Results.NotFound();
        }

        var drive = await driveRepository.GetByIdAsync(file.DriveId, cancellationToken).ConfigureAwait(false);
        if (drive is null)
        {
            // Drive existence is implied by the file lookup in v0.1, but we re-check defensively
            // — a future soft-delete on Drive would surface here as a clean 404 rather than an
            // NRE inside the registry resolver.
            return Results.NotFound();
        }

        var providerConfig = DictionaryStorageProviderConfig.FromJson(drive.ProviderConfig);
        var provider = providerRegistry.Resolve(drive.ProviderType, providerConfig);
        var stream = await provider.ReadAsync(version.StorageKey, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // enableRangeProcessing: true is the canonical streaming + range pattern for ASP.NET
        // Core's file results — it sets Accept-Ranges: bytes, parses the request's Range header,
        // and emits 206 Partial Content with Content-Range automatically. The stream must be
        // seekable; both LocalFileSystemProvider (FileStream) and InMemoryStorageProvider
        // (MemoryStream) honour that.
        return Results.File(
            stream,
            contentType: file.MimeType,
            fileDownloadName: $"{file.Name}.v{versionNumber}",
            enableRangeProcessing: true);
    }
}
