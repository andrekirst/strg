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
    /// Removes the file or directory at <paramref name="path"/>. Idempotent — does not throw if the path is absent.
    /// </summary>
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
