namespace Strg.Core.Storage;

/// <summary>
/// Abstraction over a storage backend (local filesystem, S3, etc.).
/// Callers MUST sanitize paths using <see cref="StoragePath.Parse"/> before calling any method.
/// </summary>
public interface IStorageProvider
{
    string ProviderType { get; }
    Task<IStorageFile?> GetFileAsync(string path, CancellationToken ct = default);
    Task<IStorageDirectory?> GetDirectoryAsync(string path, CancellationToken ct = default);
    Task<Stream> ReadAsync(string path, long offset = 0, CancellationToken ct = default);
    Task WriteAsync(string path, Stream content, CancellationToken ct = default);
    Task DeleteAsync(string path, CancellationToken ct = default);
    Task MoveAsync(string source, string destination, CancellationToken ct = default);
    Task CopyAsync(string source, string destination, CancellationToken ct = default);
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);
    Task CreateDirectoryAsync(string path, CancellationToken ct = default);
    IAsyncEnumerable<IStorageItem> ListAsync(string path, CancellationToken ct = default);
}
