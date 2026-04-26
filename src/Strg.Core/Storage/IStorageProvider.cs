namespace Strg.Core.Storage;

/// <summary>
/// Abstraction over a storage backend (local filesystem, S3, etc.).
/// Callers MUST sanitize paths using <see cref="StoragePath.Parse"/> before calling any method.
/// </summary>
public interface IStorageProvider
{
    /// <summary>
    /// Stable identifier for the provider type, e.g. "local", "s3". Used by the registry to match drives to providers.
    /// </summary>
    string ProviderType { get; }

    /// <summary>
    /// Returns metadata for the file at <paramref name="path"/>, or <c>null</c> if it does not exist.
    /// </summary>
    Task<IStorageFile?> GetFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns metadata for the directory at <paramref name="path"/>, or <c>null</c> if it does not exist.
    /// </summary>
    Task<IStorageDirectory?> GetDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a read stream starting at <paramref name="offset"/>. Caller is responsible for disposing the stream.
    /// Use <paramref name="offset"/> to support HTTP Range requests.
    /// </summary>
    Task<Stream> ReadAsync(string path, long offset = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/>, replacing any existing file at that path.
    /// Implementations MUST NOT buffer the entire stream in memory.
    /// </summary>
    Task WriteAsync(string path, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends <paramref name="content"/> to the file at <paramref name="path"/>, creating it if absent.
    /// Implementations MUST NOT buffer the entire stream in memory and MUST NOT clobber the existing
    /// content of the file (in contrast to <see cref="WriteAsync"/>).
    /// </summary>
    /// <remarks>
    /// Required by the TUS upload pipeline (STRG-034): a multi-chunk PATCH stream needs to be
    /// accumulated at a temp key across HTTP requests before the encrypting writer runs once at
    /// finalize. Without this primitive the only option is read-existing → concat → re-write, which
    /// is O(N²) bytes for an N-chunk upload. Concurrent appenders are out-of-scope: tusdotnet
    /// serialises chunks per upload via its file-lock provider, so single-writer semantics are
    /// sufficient. The local-FS provider uses <c>FileMode.Append</c>; the in-memory provider
    /// concatenates byte arrays under its per-key lock.
    /// </remarks>
    Task AppendAsync(string path, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the file or directory at <paramref name="path"/>. Idempotent — does not throw if the path is absent.
    /// </summary>
    /// <remarks>
    /// <b>Idempotency is load-bearing, not a convenience.</b> Implementations MUST NOT throw when
    /// the target path is absent. The per-version retry contract on
    /// <see cref="Strg.Core.Services.IFileVersionStore.PruneVersionsAsync"/> — where a
    /// mid-batch failure leaves the already-deleted blobs reachable by a resumed prune —
    /// requires this behavior: a second call targeting an already-absent blob must return
    /// silently so the retry can advance to the next un-pruned version instead of looping
    /// forever at iteration <c>k</c>. Any future provider that throws on missing would break
    /// this contract and reintroduce the orphan-row drift the per-version scope was designed
    /// to prevent. Linux <c>File.Delete</c>, S3 <c>DeleteObject</c>, and the in-memory test
    /// provider all honor this today.
    /// </remarks>
    Task DeleteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the file or directory from <paramref name="source"/> to <paramref name="destination"/>.
    /// </summary>
    Task MoveAsync(string source, string destination, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies the file or directory from <paramref name="source"/> to <paramref name="destination"/>.
    /// </summary>
    Task CopyAsync(string source, string destination, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <c>true</c> if a file or directory exists at <paramref name="path"/>.
    /// </summary>
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a directory at <paramref name="path"/>, including any missing parent directories.
    /// </summary>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams the children of <paramref name="path"/>. Yielded items may be files or subdirectories.
    /// </summary>
    IAsyncEnumerable<IStorageItem> ListAsync(string path, CancellationToken cancellationToken = default);
}
