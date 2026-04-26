using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Strg.Core.Storage;

namespace Strg.Infrastructure.Storage;

/// <summary>
/// In-memory <see cref="IStorageProvider"/> implementation for tests. Stores file bytes in a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> keyed by the normalized
/// <see cref="StoragePath.Value"/>. Intentionally lives in <c>Strg.Infrastructure</c> so
/// integration tests can resolve it through the registry with the same factory shape as the
/// production provider, but it is <b>never</b> registered in <c>Program.cs</c> — test DI wires
/// it in explicitly.
///
/// <para>Case sensitivity mirrors <see cref="StoragePath"/>'s equality (ordinal-ignore-case),
/// which keeps the test behaviour predictable on both Windows and Linux hosts even though the
/// real filesystem behaviour differs between them.</para>
///
/// <para>Quota-testing note: writes capture the full byte array (not a reference), so tests can
/// rewrite a buffer in-place after WriteAsync without corrupting the stored content.</para>
/// </summary>
public sealed class InMemoryStorageProvider : IStorageProvider
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private readonly ConcurrentDictionary<string, FileEntry> _files = new(PathComparer);
    private readonly ConcurrentDictionary<string, DirEntry> _dirs = new(PathComparer);

    public string ProviderType => "memory";

    /// <summary>Drops every file and directory. Test-only teardown hook.</summary>
    public void ClearAll()
    {
        _files.Clear();
        _dirs.Clear();
    }

    public Task<IStorageFile?> GetFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var key = StoragePath.Parse(path).Value;
        if (!_files.TryGetValue(key, out var entry))
        {
            return Task.FromResult<IStorageFile?>(null);
        }
        return Task.FromResult<IStorageFile?>(ToStorageFile(key, entry));
    }

    public Task<IStorageDirectory?> GetDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var key = StoragePath.Parse(path).Value;
        if (!_dirs.TryGetValue(key, out var entry))
        {
            // Implicit directories: if any file lives under this prefix, treat the prefix as a
            // directory even without an explicit CreateDirectoryAsync call. Matches the real
            // filesystem's "parent exists if children exist" mental model.
            if (_files.Keys.Any(k => IsImmediateOrDeeperChild(key, k)))
            {
                var now = DateTimeOffset.UtcNow;
                return Task.FromResult<IStorageDirectory?>(ToStorageDirectory(key, new DirEntry(now, now)));
            }
            return Task.FromResult<IStorageDirectory?>(null);
        }
        return Task.FromResult<IStorageDirectory?>(ToStorageDirectory(key, entry));
    }

    public async Task<Stream> ReadAsync(string path, long offset = 0, CancellationToken cancellationToken = default)
    {
        var key = StoragePath.Parse(path).Value;
        if (!_files.TryGetValue(key, out var entry))
        {
            throw new FileNotFoundException($"File not found: {path}");
        }

        var stream = new MemoryStream(entry.Content, writable: false);
        if (offset > 0)
        {
            stream.Seek(offset, SeekOrigin.Begin);
        }
        return await Task.FromResult<Stream>(stream).ConfigureAwait(false);
    }

    public async Task WriteAsync(string path, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var key = StoragePath.Parse(path).Value;

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        _files.AddOrUpdate(
            key,
            _ => new FileEntry(buffer.ToArray(), now, now),
            (_, existing) => existing with { Content = buffer.ToArray(), UpdatedAt = now });

        // Parent directory implicitly exists — materialize it in the dir map so GetDirectoryAsync
        // returns timestamps that reflect the write. Walk every ancestor so nested paths work.
        foreach (var ancestor in EnumerateAncestors(key))
        {
            _dirs.AddOrUpdate(
                ancestor,
                _ => new DirEntry(now, now),
                (_, existing) => existing with { UpdatedAt = now });
        }
    }

    public async Task AppendAsync(string path, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var key = StoragePath.Parse(path).Value;

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var newBytes = buffer.ToArray();

        var now = DateTimeOffset.UtcNow;
        _files.AddOrUpdate(
            key,
            _ => new FileEntry(newBytes, now, now),
            (_, existing) =>
            {
                // Concat under the per-key update lock — AddOrUpdate's update factory is invoked
                // atomically per key, so two concurrent appenders against the same key serialize.
                var combined = new byte[existing.Content.Length + newBytes.Length];
                Buffer.BlockCopy(existing.Content, 0, combined, 0, existing.Content.Length);
                Buffer.BlockCopy(newBytes, 0, combined, existing.Content.Length, newBytes.Length);
                return existing with { Content = combined, UpdatedAt = now };
            });

        foreach (var ancestor in EnumerateAncestors(key))
        {
            _dirs.AddOrUpdate(
                ancestor,
                _ => new DirEntry(now, now),
                (_, existing) => existing with { UpdatedAt = now });
        }
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        var key = StoragePath.Parse(path).Value;
        _files.TryRemove(key, out _);

        // Delete every file and directory below this prefix — matches the recursive-delete
        // semantics of the local provider (Directory.Delete(path, recursive: true)).
        foreach (var fileKey in _files.Keys.Where(k => IsImmediateOrDeeperChild(key, k)).ToList())
        {
            _files.TryRemove(fileKey, out _);
        }
        foreach (var dirKey in _dirs.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)
            || IsImmediateOrDeeperChild(key, k)).ToList())
        {
            _dirs.TryRemove(dirKey, out _);
        }

        return Task.CompletedTask;
    }

    public Task MoveAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        var sourceKey = StoragePath.Parse(source).Value;
        var destKey = StoragePath.Parse(destination).Value;

        if (_files.TryRemove(sourceKey, out var entry))
        {
            if (!_files.TryAdd(destKey, entry))
            {
                _files[sourceKey] = entry;
                throw new IOException($"Destination already exists: {destination}");
            }
            return Task.CompletedTask;
        }

        throw new FileNotFoundException($"Source not found: {source}");
    }

    public Task CopyAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        var sourceKey = StoragePath.Parse(source).Value;
        var destKey = StoragePath.Parse(destination).Value;

        if (!_files.TryGetValue(sourceKey, out var entry))
        {
            throw new FileNotFoundException($"Source not found: {source}");
        }

        // Clone the byte array so mutating one payload doesn't corrupt the other. Stored entries
        // are already captured by value via ToArray() in WriteAsync, but CopyAsync is a distinct
        // operation and tests may rely on independence.
        var copied = (byte[])entry.Content.Clone();
        if (!_files.TryAdd(destKey, entry with { Content = copied }))
        {
            throw new IOException($"Destination already exists: {destination}");
        }
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        // Empty input is the documented sentinel for the drive root — see
        // WebDavUriParser.ExtractValidatedPath:48-50 for the same guard precedent. Without this,
        // StoragePath.Parse rejects empty (the fail-closed contract added in commit 40ed3b7),
        // which would block `ExistsAsync("")` semantics ("does root exist?") that callers rely on.
        var key = string.IsNullOrEmpty(path) ? string.Empty : StoragePath.Parse(path).Value;
        if (_files.ContainsKey(key) || _dirs.ContainsKey(key))
        {
            return Task.FromResult(true);
        }
        // An implicit parent — a directory exists if any file lives beneath it.
        return Task.FromResult(_files.Keys.Any(k => IsImmediateOrDeeperChild(key, k)));
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        // Empty input → create the root directory, which is an implicit no-op. EnumerateSelfAndAncestors
        // returns no ancestors for empty input, so the loop body is naturally skipped.
        var key = string.IsNullOrEmpty(path) ? string.Empty : StoragePath.Parse(path).Value;
        var now = DateTimeOffset.UtcNow;
        foreach (var ancestor in EnumerateSelfAndAncestors(key))
        {
            _dirs.TryAdd(ancestor, new DirEntry(now, now));
        }
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<IStorageItem> ListAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Empty input is the documented sentinel for "list at the drive root" — the downstream
        // `key.Length == 0` branch on the next line is already designed to handle this case.
        // The guard exists because StoragePath.Parse itself rejects empty (commit 40ed3b7's
        // fail-closed contract), so we must short-circuit before calling Parse.
        var key = string.IsNullOrEmpty(path) ? string.Empty : StoragePath.Parse(path).Value;
        var prefix = key.Length == 0 ? string.Empty : key + "/";

        // Snapshot child names so concurrent mutations during enumeration don't trip us up.
        var seen = new HashSet<string>(PathComparer);

        foreach (var fileKey in _files.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsImmediateChild(prefix, fileKey))
            {
                continue;
            }
            if (seen.Add(fileKey) && _files.TryGetValue(fileKey, out var entry))
            {
                yield return ToStorageFile(fileKey, entry);
            }
        }

        foreach (var dirKey in _dirs.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsImmediateChild(prefix, dirKey))
            {
                continue;
            }
            if (seen.Add(dirKey) && _dirs.TryGetValue(dirKey, out var entry))
            {
                yield return ToStorageDirectory(dirKey, entry);
            }
        }

        // Implicit directory discovery: files deeper than immediate children imply intermediate
        // directories that were never explicitly created. Surface them so tests listing
        // "docs/" see "docs/2024/" even if only "docs/2024/report.pdf" was written.
        foreach (var fileKey in _files.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fileKey.Length <= prefix.Length || !fileKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var remainder = fileKey[prefix.Length..];
            var slashIndex = remainder.IndexOf('/');
            if (slashIndex <= 0)
            {
                continue;
            }
            var implicitDir = prefix + remainder[..slashIndex];
            if (seen.Add(implicitDir))
            {
                var now = DateTimeOffset.UtcNow;
                yield return ToStorageDirectory(implicitDir, new DirEntry(now, now));
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static bool IsImmediateChild(string prefix, string candidate)
    {
        if (prefix.Length == 0)
        {
            return !candidate.Contains('/');
        }
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var remainder = candidate[prefix.Length..];
        return remainder.Length > 0 && !remainder.Contains('/');
    }

    private static bool IsImmediateOrDeeperChild(string parent, string candidate)
    {
        if (parent.Length == 0)
        {
            return candidate.Length > 0;
        }
        return candidate.Length > parent.Length
            && candidate.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateAncestors(string path)
    {
        var idx = path.LastIndexOf('/');
        while (idx > 0)
        {
            yield return path[..idx];
            idx = path.LastIndexOf('/', idx - 1);
        }
    }

    private static IEnumerable<string> EnumerateSelfAndAncestors(string path)
    {
        if (path.Length == 0)
        {
            yield break;
        }
        yield return path;
        foreach (var ancestor in EnumerateAncestors(path))
        {
            yield return ancestor;
        }
    }

    private static IStorageFile ToStorageFile(string path, FileEntry entry) => new InMemoryStorageFile(
        Name: LeafName(path),
        Path: path,
        CreatedAt: entry.CreatedAt,
        UpdatedAt: entry.UpdatedAt,
        Size: entry.Content.LongLength);

    private static IStorageDirectory ToStorageDirectory(string path, DirEntry entry) => new InMemoryStorageDirectory(
        Name: LeafName(path),
        Path: path,
        CreatedAt: entry.CreatedAt,
        UpdatedAt: entry.UpdatedAt);

    private static string LeafName(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx >= 0 ? path[(idx + 1)..] : path;
    }

    private sealed record FileEntry(byte[] Content, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    private sealed record DirEntry(DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    private sealed record InMemoryStorageFile(
        string Name,
        string Path,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        long Size) : IStorageFile
    {
        public bool IsDirectory => false;
        public string? ContentHash => null;
    }

    private sealed record InMemoryStorageDirectory(
        string Name,
        string Path,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt) : IStorageDirectory
    {
        public bool IsDirectory => true;
    }
}
