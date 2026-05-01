using Strg.Core;
using Strg.Core.Domain;
using Strg.Plugin.Abstractions.Storage;
using Strg.Plugin.Abstractions.Storage.Encryption;

namespace Strg.Application.Features.Files.Download;

/// <inheritdoc cref="IFileDownloadResolver"/>
internal sealed class FileDownloadResolver(
    IFileRepository fileRepository,
    IDriveRepository driveRepository,
    IFileVersionRepository versionRepository,
    IFileKeyRepository fileKeyRepository,
    IStorageProviderRegistry providerRegistry,
    IEncryptingFileWriterFactory encryptingWriterFactory)
    : IFileDownloadResolver
{
    public async Task<Result<FileDownloadResult, DownloadFailure>> ResolveAsync(
        Guid driveId,
        Guid fileId,
        DownloadRange? range,
        CancellationToken cancellationToken)
    {
        var drive = await driveRepository.GetByIdAsync(driveId, cancellationToken).ConfigureAwait(false);
        if (drive is null)
        {
            return Result<FileDownloadResult, DownloadFailure>.Failure(
                new DownloadFailure.NotFound("Drive not found."));
        }

        var file = await fileRepository.GetByIdAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (file is null || file.DriveId != driveId)
        {
            // Cross-drive id mismatch is also collapsed to NotFound — preventing the capability-
            // confusion shape where a caller addresses a known fileId via an unrelated driveId.
            return Result<FileDownloadResult, DownloadFailure>.Failure(
                new DownloadFailure.NotFound("File not found."));
        }

        if (file.IsDirectory)
        {
            return Result<FileDownloadResult, DownloadFailure>.Failure(
                new DownloadFailure.IsDirectory("Directories cannot be downloaded as content."));
        }

        if (string.IsNullOrEmpty(file.StorageKey))
        {
            // Half-uploaded rows expose a 404 to the client (same shape as unknown id) so the
            // wire contract does not leak an "exists but pending" state.
            return Result<FileDownloadResult, DownloadFailure>.Failure(
                new DownloadFailure.NotFound("File not found."));
        }

        var resolved = ResolveRange(range, file.Size);
        if (resolved.IsUnsatisfiable)
        {
            return Result<FileDownloadResult, DownloadFailure>.Failure(
                new DownloadFailure.RangeNotSatisfiable(file.Size));
        }

        Stream sourceStream;
        try
        {
            sourceStream = await OpenReadStreamAsync(drive, file, resolved.Start, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // OpenReadStreamAsync throws InvalidOperationException for missing-version /
            // missing-key — server invariant violations. The exception message carries
            // identifiers for log correlation; never key material.
            return Result<FileDownloadResult, DownloadFailure>.Failure(
                new DownloadFailure.InternalState(ex.Message));
        }

        return Result<FileDownloadResult, DownloadFailure>.Success(new FileDownloadResult
        {
            Content = sourceStream,
            Size = file.Size,
            MimeType = file.MimeType,
            Filename = file.Name,
            FileId = file.Id,
            DriveId = file.DriveId,
            PartialStart = resolved.IsPartial ? resolved.Start : null,
            PartialEnd = resolved.IsPartial ? resolved.End : null,
        });
    }

    private async Task<Stream> OpenReadStreamAsync(
        Drive drive,
        FileItem file,
        long offset,
        CancellationToken cancellationToken)
    {
        var providerConfig = DictionaryStorageProviderConfig.FromJson(drive.ProviderConfig);
        var provider = providerRegistry.Resolve(drive.ProviderType, providerConfig);

        if (!drive.EncryptionEnabled)
        {
            return await provider.ReadAsync(file.StorageKey!, offset, cancellationToken).ConfigureAwait(false);
        }

        // Tenant safety order: file → version → key. FileVersion / FileKey inherit Entity (NOT
        // TenantedEntity); reaching them via the tenant-filtered FileItem above is the only safe
        // path. The defensive VersionCount check guards against an impossible-at-v0.1 zero state.
        if (file.VersionCount <= 0)
        {
            throw new InvalidOperationException($"FileItem {file.Id} has VersionCount={file.VersionCount}.");
        }

        var version = await versionRepository.GetAsync(file.Id, file.VersionCount, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"FileVersion (file={file.Id}, version={file.VersionCount}) is missing.");

        var key = await fileKeyRepository.GetByFileVersionAsync(version.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"FileKey for version {version.Id} is missing.");

        // Bind the writer to the drive's actual provider — the per-call factory closes the
        // gap that DI cannot (storage providers are per-drive, not registered as services).
        // Without this, a singleton-injected writer would read from whatever provider it was
        // constructed with, silently bypassing the per-drive routing the rest of the resolver
        // performs.
        var encryptingWriter = encryptingWriterFactory.Create(provider);
        return await encryptingWriter.ReadAsync(file.StorageKey!, key.EncryptedDek, key.Algorithm, offset, cancellationToken).ConfigureAwait(false);
    }

    private static ResolvedRange ResolveRange(DownloadRange? range, long size)
    {
        if (range is null)
        {
            return ResolvedRange.Full();
        }

        if (size == 0)
        {
            return ResolvedRange.Unsatisfiable();
        }

        long start;
        long end;
        if (range.From.HasValue)
        {
            start = range.From.Value;
            if (start >= size)
            {
                return ResolvedRange.Unsatisfiable();
            }
            end = range.To.HasValue ? Math.Min(range.To.Value, size - 1) : size - 1;
            if (end < start)
            {
                return ResolvedRange.Unsatisfiable();
            }
        }
        else if (range.To.HasValue)
        {
            // Suffix: bytes=-N, last N bytes (clamped to 0 if N > size).
            var suffix = range.To.Value;
            if (suffix == 0)
            {
                return ResolvedRange.Unsatisfiable();
            }
            start = Math.Max(0, size - suffix);
            end = size - 1;
        }
        else
        {
            // The validator forbids both nulls; this branch is unreachable but kept defensive.
            return ResolvedRange.Full();
        }

        return ResolvedRange.Partial(start, end);
    }

    private readonly record struct ResolvedRange(long Start, long End, bool IsPartial, bool IsUnsatisfiable)
    {
        public static ResolvedRange Full() => new(0, 0, IsPartial: false, IsUnsatisfiable: false);
        public static ResolvedRange Partial(long start, long end) => new(start, end, IsPartial: true, IsUnsatisfiable: false);
        public static ResolvedRange Unsatisfiable() => new(0, 0, IsPartial: false, IsUnsatisfiable: true);
    }
}
