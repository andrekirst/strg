using Strg.Application.Features.Files.Download;
using Strg.Core;
using Strg.Core.Domain;
using Strg.Plugin.Abstractions.Storage;
using Strg.Plugin.Abstractions.Internal.Encryption;

namespace Strg.Application.Features.Files.DownloadVersion;

/// <inheritdoc cref="IFileVersionDownloadResolver"/>
internal sealed class FileVersionDownloadResolver(
    IFileRepository fileRepository,
    IDriveRepository driveRepository,
    IFileVersionRepository versionRepository,
    IFileKeyRepository fileKeyRepository,
    IStorageProviderRegistry providerRegistry,
    IEncryptingFileWriterFactory encryptingWriterFactory)
    : IFileVersionDownloadResolver
{
    public async Task<Result<FileDownloadResult, DownloadFailure>> ResolveAsync(
        Guid driveId,
        Guid fileId,
        int versionNumber,
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
            // Cross-drive id mismatch is collapsed to NotFound — same capability-confusion
            // hardening as FileDownloadResolver.
            return Result<FileDownloadResult, DownloadFailure>.Failure(
                new DownloadFailure.NotFound("File not found."));
        }

        if (file.IsDirectory)
        {
            // Defensive — directories have no version rows, so we'd hit the version lookup
            // miss below. Returning IsDirectory here keeps the failure shape predictable for
            // callers who pre-check via the same union.
            return Result<FileDownloadResult, DownloadFailure>.Failure(
                new DownloadFailure.IsDirectory("Directories cannot be downloaded as content."));
        }

        var version = await versionRepository.GetAsync(file.Id, versionNumber, cancellationToken).ConfigureAwait(false);
        if (version is null)
        {
            return Result<FileDownloadResult, DownloadFailure>.Failure(
                new DownloadFailure.NotFound("File version not found."));
        }

        var resolved = ResolveRange(range, version.Size);
        if (resolved.IsUnsatisfiable)
        {
            return Result<FileDownloadResult, DownloadFailure>.Failure(
                new DownloadFailure.RangeNotSatisfiable(version.Size));
        }

        Stream sourceStream;
        try
        {
            sourceStream = await OpenReadStreamAsync(drive, version, resolved.Start, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return Result<FileDownloadResult, DownloadFailure>.Failure(
                new DownloadFailure.InternalState(ex.Message));
        }

        return Result<FileDownloadResult, DownloadFailure>.Success(new FileDownloadResult
        {
            Content = sourceStream,
            Size = version.Size,
            MimeType = file.MimeType,
            // Filename includes the version suffix per the issue spec ("{name}.v{n}") so
            // a client saving the response distinguishes the historical version from the
            // current head download.
            Filename = $"{file.Name}.v{version.VersionNumber}",
            FileId = file.Id,
            DriveId = file.DriveId,
            PartialStart = resolved.IsPartial ? resolved.Start : null,
            PartialEnd = resolved.IsPartial ? resolved.End : null,
        });
    }

    private async Task<Stream> OpenReadStreamAsync(
        Drive drive,
        FileVersion version,
        long offset,
        CancellationToken cancellationToken)
    {
        var providerConfig = DictionaryStorageProviderConfig.FromJson(drive.ProviderConfig);
        var provider = providerRegistry.Resolve(drive.ProviderType, providerConfig);

        if (!drive.EncryptionEnabled)
        {
            return await provider.ReadAsync(version.StorageKey, offset, cancellationToken).ConfigureAwait(false);
        }

        // Encrypted path: each FileVersion has its own FileKey row (separate DEK per version,
        // per STRG-026). Missing key for an encrypted-drive version is a server invariant
        // violation — collapse to InternalState 500 with identifier-only detail.
        var key = await fileKeyRepository.GetByFileVersionAsync(version.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"FileKey for version {version.Id} is missing.");

        var encryptingWriter = encryptingWriterFactory.Create(provider);
        return await encryptingWriter.ReadAsync(version.StorageKey, key.EncryptedDek, key.Algorithm, offset, cancellationToken).ConfigureAwait(false);
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
