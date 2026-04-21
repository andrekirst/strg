using System.Runtime.CompilerServices;
using Strg.Core.Storage;

namespace Strg.Infrastructure.Storage;

/// <summary>
/// <see cref="IStorageProvider"/> backed by the local filesystem rooted at a per-drive base path.
/// The base path is supplied from <c>Drive.ProviderConfig["rootPath"]</c> by the registry factory.
///
/// <para><b>Path safety (defense in depth).</b> Every incoming path flows through
/// <see cref="StoragePath.Parse"/> (rejects <c>..</c>, absolute paths, null bytes, reserved names)
/// AND a post-resolution <see cref="string.StartsWith(string, StringComparison)"/> check against
/// the normalized base path. Either alone would be a bypass: StoragePath.Parse operates on the
/// string form, but <see cref="Path.GetFullPath(string)"/> can still produce a path outside the
/// root if the input somehow smuggles traversal past the first filter (belt-and-braces).</para>
///
/// <para><b>Symlinks are rejected, not followed.</b> <see cref="Path.GetFullPath(string)"/> does
/// NOT resolve symlinks — it only normalizes the textual form. A symlink inside <c>basePath</c>
/// can therefore pass the <c>StartsWith</c> check and still point outside. We check
/// <see cref="FileSystemInfo.LinkTarget"/> on every materialized node and treat any non-null
/// target as <c>not found</c> to stay consistent with the "file does not exist" contract.</para>
/// </summary>
public sealed class LocalFileSystemProvider : IStorageProvider
{
    private static readonly StringComparison FsComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _basePath;
    private readonly string _basePathWithSeparator;

    /// <summary>
    /// Creates a provider rooted at <paramref name="basePath"/>. The directory is created if
    /// missing — provider construction must be side-effect safe for DI-scoped factories, but
    /// creating the root on first use is the standard onboarding contract for a fresh drive.
    /// </summary>
    public LocalFileSystemProvider(string basePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

        var absolute = Path.GetFullPath(basePath);
        Directory.CreateDirectory(absolute);

        _basePath = TrimTrailingSeparator(absolute);
        _basePathWithSeparator = _basePath + Path.DirectorySeparatorChar;
    }

    public string ProviderType => "local";

    public Task<IStorageFile?> GetFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = ResolvePath(path);
        if (!File.Exists(full))
        {
            return Task.FromResult<IStorageFile?>(null);
        }

        var info = new FileInfo(full);
        if (info.LinkTarget is not null)
        {
            return Task.FromResult<IStorageFile?>(null);
        }

        var normalized = NormalizeRelative(path);
        return Task.FromResult<IStorageFile?>(LocalStorageFile.FromFileInfo(normalized, info));
    }

    public Task<IStorageDirectory?> GetDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = ResolvePath(path);
        if (!Directory.Exists(full))
        {
            return Task.FromResult<IStorageDirectory?>(null);
        }

        var info = new DirectoryInfo(full);
        if (info.LinkTarget is not null)
        {
            return Task.FromResult<IStorageDirectory?>(null);
        }

        var normalized = NormalizeRelative(path);
        return Task.FromResult<IStorageDirectory?>(LocalStorageDirectory.FromDirectoryInfo(normalized, info));
    }

    public Task<Stream> ReadAsync(string path, long offset = 0, CancellationToken cancellationToken = default)
    {
        var full = ResolvePath(path);

        // LinkTarget check runs BEFORE the FileStream open so a symlink can't be read through
        // even if the filesystem would otherwise happily follow it.
        if (new FileInfo(full).LinkTarget is not null)
        {
            throw new FileNotFoundException($"File not found: {path}");
        }

        // FileShare.Read permits concurrent reads (multiple clients streaming the same blob) but
        // blocks writes — keeping read offsets stable within a single HTTP Range request.
        var stream = new FileStream(
            full,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        if (offset > 0)
        {
            stream.Seek(offset, SeekOrigin.Begin);
        }

        return Task.FromResult<Stream>(stream);
    }

    public async Task WriteAsync(string path, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var full = ResolveChildPath(path);

        // Intermediate directories materialize automatically so the caller's mental model matches
        // "write file at logical path"; otherwise the first upload into a new folder would require
        // a prior CreateDirectoryAsync round-trip.
        var parent = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        // FileMode.Create truncates an existing file — matches the WriteAsync contract ("replacing
        // any existing file at that path") and intentionally does not preserve prior content.
        // FileShare.None locks the file for the duration of the write so a concurrent reader can't
        // observe a partially-written stream (cf. FileShare.Read on the read path).
        await using var target = new FileStream(
            full,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        await content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        // Must use ResolveChildPath so `""`, `"."`, `"./"` etc. can't resolve to the base
        // directory and take the whole drive with them on Directory.Delete(recursive: true).
        var full = ResolveChildPath(path);

        if (File.Exists(full))
        {
            File.Delete(full);
        }
        else if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
        // Silent no-op on missing target — DeleteAsync is documented as idempotent.

        return Task.CompletedTask;
    }

    public Task MoveAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        // Both endpoints need the root guard: moving the base directory away is as bad as
        // overwriting it, and Directory.Move onto the base path would either fail opaquely
        // or — on some filesystems — relocate the entire drive.
        var sourceFull = ResolveChildPath(source);
        var destinationFull = ResolveChildPath(destination);

        var parent = Path.GetDirectoryName(destinationFull);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (File.Exists(sourceFull))
        {
            // overwrite: false — move must not silently clobber an existing destination. Quota
            // accounting and versioning decisions are made one layer up; this layer stays dumb.
            File.Move(sourceFull, destinationFull, overwrite: false);
        }
        else if (Directory.Exists(sourceFull))
        {
            Directory.Move(sourceFull, destinationFull);
        }
        else
        {
            throw new FileNotFoundException($"Source not found: {source}");
        }

        return Task.CompletedTask;
    }

    public Task CopyAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        var sourceFull = ResolvePath(source);
        var destinationFull = ResolvePath(destination);

        var parent = Path.GetDirectoryName(destinationFull);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (File.Exists(sourceFull))
        {
            File.Copy(sourceFull, destinationFull, overwrite: false);
        }
        else if (Directory.Exists(sourceFull))
        {
            CopyDirectoryRecursive(sourceFull, destinationFull);
        }
        else
        {
            throw new FileNotFoundException($"Source not found: {source}");
        }

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = ResolvePath(path);
        return Task.FromResult(File.Exists(full) || Directory.Exists(full));
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = ResolvePath(path);
        Directory.CreateDirectory(full);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<IStorageItem> ListAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var full = ResolvePath(path);
        if (!Directory.Exists(full))
        {
            yield break;
        }

        var normalizedParent = NormalizeRelative(path);

        // EnumerateFileSystemEntries is streaming — the iterator does not materialize the full
        // child list up front, so directories with 10_000+ entries stay O(1) in memory here.
        // ToAsyncEnumerable isn't used because the inner calls are synchronous; instead we yield
        // directly and rely on [EnumeratorCancellation] to honour cancellation between items.
        foreach (var entry in Directory.EnumerateFileSystemEntries(full))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(entry);
            var childRelative = string.IsNullOrEmpty(normalizedParent) ? name : $"{normalizedParent}/{name}";

            if (File.Exists(entry))
            {
                var info = new FileInfo(entry);
                if (info.LinkTarget is not null)
                {
                    continue;
                }
                yield return LocalStorageFile.FromFileInfo(childRelative, info);
            }
            else if (Directory.Exists(entry))
            {
                var info = new DirectoryInfo(entry);
                if (info.LinkTarget is not null)
                {
                    continue;
                }
                yield return LocalStorageDirectory.FromDirectoryInfo(childRelative, info);
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="relativePath"/> through <see cref="StoragePath.Parse"/>, joins it to
    /// the base path, and reconfirms the resolved absolute path is still under the root. Returns
    /// the absolute path suitable for BCL <see cref="File"/>/<see cref="Directory"/> calls.
    /// </summary>
    private string ResolvePath(string relativePath)
    {
        var parsed = StoragePath.Parse(relativePath);

        // Empty relative path resolves to the base path itself — Path.Combine handles this, but
        // Path.GetFullPath on an empty string throws, so we short-circuit.
        var combined = string.IsNullOrEmpty(parsed.Value)
            ? _basePath
            : Path.Combine(_basePath, parsed.Value);

        var full = Path.GetFullPath(combined);

        // The "base + separator" form is load-bearing: without the trailing separator,
        // "/var/strg/drive" would match "/var/strg/drive-evil" via StartsWith.
        if (!(full.Equals(_basePath, FsComparison) || full.StartsWith(_basePathWithSeparator, FsComparison)))
        {
            throw new StoragePathException($"Path escapes base directory: {relativePath}");
        }

        return full;
    }

    /// <summary>
    /// <see cref="ResolvePath"/> + a guard against paths that resolve to the drive root. Destructive
    /// or mutating ops (<c>Delete</c>, <c>Write</c>, <c>Move</c>) must never target the base itself —
    /// <c>DeleteAsync("")</c> or <c>DeleteAsync(".")</c> would otherwise take the entire drive with
    /// it via <c>Directory.Delete(recursive: true)</c>. Read-only ops (<c>GetFileAsync</c>,
    /// <c>ExistsAsync</c>, <c>ListAsync</c>) legitimately accept root and continue to use the
    /// plain <see cref="ResolvePath"/>.
    /// </summary>
    private string ResolveChildPath(string relativePath)
    {
        var full = ResolvePath(relativePath);
        if (full.Equals(_basePath, FsComparison))
        {
            throw new StoragePathException($"Operation targets the drive root: '{relativePath}'");
        }
        return full;
    }

    private static string NormalizeRelative(string path) => StoragePath.Parse(path).Value;

    private static string TrimTrailingSeparator(string path) =>
        path.Length > 1 && (path[^1] == Path.DirectorySeparatorChar || path[^1] == Path.AltDirectorySeparatorChar)
            ? path[..^1]
            : path;

    private static void CopyDirectoryRecursive(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, target, overwrite: false);
        }

        foreach (var subDir in Directory.EnumerateDirectories(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(subDir));
            CopyDirectoryRecursive(subDir, target);
        }
    }
}
