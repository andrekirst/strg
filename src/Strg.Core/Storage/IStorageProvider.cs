namespace Strg.Core.Storage;

/// <summary>
/// Abstraction over a storage backend (local filesystem, S3, etc.).
/// Callers MUST sanitize paths using <see cref="StoragePath.Parse"/> before calling any method.
/// </summary>
public interface IStorageProvider
{
    string ProviderType { get; }
    Task<IStorageFile?> GetFileAsync(string path, CancellationToken cancellationToken = default);
    Task<IStorageDirectory?> GetDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task<Stream> ReadAsync(string path, long offset = 0, CancellationToken cancellationToken = default);
    Task WriteAsync(string path, Stream content, CancellationToken cancellationToken = default);
    Task DeleteAsync(string path, CancellationToken cancellationToken = default);
    Task MoveAsync(string source, string destination, CancellationToken cancellationToken = default);
    Task CopyAsync(string source, string destination, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);
    IAsyncEnumerable<IStorageItem> ListAsync(string path, CancellationToken cancellationToken = default);
}
