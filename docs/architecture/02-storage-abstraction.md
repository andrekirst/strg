# Storage Abstraction

## Design

The `IStorageProvider` interface is the core abstraction for all file I/O. Every storage backend — local filesystem, S3, Google Drive, OneDrive, IPFS, ZIP archives — implements this interface.

This means:
- Encryption wraps any provider transparently
- ZIP virtual filesystem is itself a provider
- Tests use an in-memory provider
- Plugins add new providers without touching core code

---

## Interface Definition

```csharp
namespace Strg.Core.Storage;

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

public interface IStorageFile : IStorageItem
{
    long Size { get; }
    string? ContentHash { get; }  // SHA-256 hex
}

public interface IStorageDirectory : IStorageItem { }

public interface IStorageItem
{
    string Name { get; }
    string Path { get; }
    bool IsDirectory { get; }
    DateTimeOffset CreatedAt { get; }
    DateTimeOffset UpdatedAt { get; }
}
```

---

## Provider Registry

Providers are registered in DI by type name. When a drive is created, its `provider_type` is used to resolve the correct provider:

```csharp
// Registration (built-in)
services.AddStorageProvider<LocalFileSystemProvider>("local");

// Registration (plugin)
services.AddStorageProvider<SftpStorageProvider>("sftp");

// Resolution
var provider = providerRegistry.GetProvider(drive.ProviderType);
```

---

## Encryption Layer

The `EncryptingStorageProvider` is a decorator that wraps any `IStorageProvider`:

```
drive.EncryptionEnabled = true
  → EncryptingStorageProvider
      → wraps: LocalFileSystemProvider (or any backend)
      → AES-256-GCM per-file
      → DEK stored encrypted with KEK in file_versions.storage_key
```

The caller always receives plaintext. The encryption layer is invisible to all higher-level code.

```csharp
public class EncryptingStorageProvider : IStorageProvider
{
    private readonly IStorageProvider _inner;
    private readonly IKeyProvider _keyProvider;

    public async Task WriteAsync(string path, Stream content, CancellationToken ct)
    {
        var dek = _keyProvider.GenerateDataKey();
        var encryptedStream = new AesGcmStream(content, dek);
        await _inner.WriteAsync(path, encryptedStream, ct);
        await _keyProvider.StoreEncryptedDekAsync(path, dek, ct);
    }
}
```

---

## ZIP Virtual Filesystem

ZIP files stored in strg can be opened as virtual directories. The `ZipStorageProvider` is instantiated on demand for a specific `.zip` file:

```
GET /api/v1/drives/{driveId}/files/{zipFileId}/entries
→ ZipStorageProvider.ListAsync("/")
→ returns entries in the ZIP as IStorageDirectory / IStorageFile
```

Supported operations:
- `ListAsync` — list entries
- `ReadAsync` — read a file inside the ZIP (streaming, no full extraction)
- `WriteAsync` — add or replace a file inside the ZIP (copy-on-write: new ZIP written to temp, then replaces original)
- `DeleteAsync` — remove entry from ZIP (copy-on-write)

---

## Path Resolution

Paths within a drive are always relative to the drive root. The path `/docs/report.pdf` in drive `home-nas` resolves to:

```
LocalFileSystemProvider → /var/strg/drives/home-nas/docs/report.pdf
S3Provider              → s3://my-bucket/home-nas/docs/report.pdf
GoogleDriveProvider     → (Google Drive API, path mapped to file ID)
```

Path normalization rejects:
- `..` (traversal)
- Null bytes
- Absolute paths starting with `/` when used as relative paths
- OS-reserved names (Windows: `CON`, `PRN`, `AUX`, etc.)

---

## Provider Configuration

Each drive stores its provider configuration as JSONB. Schemas are validated at drive creation time by the provider:

```json
// LocalFileSystemProvider
{
  "basePath": "/var/strg/drives/my-drive"
}

// S3Provider (plugin)
{
  "bucket": "my-strg-bucket",
  "region": "eu-central-1",
  "prefix": "drives/my-drive/",
  "credentials": { "source": "env", "keyVar": "AWS_ACCESS_KEY_ID", "secretVar": "AWS_SECRET_ACCESS_KEY" }
}

// SftpProvider (plugin)
{
  "host": "nas.home.local",
  "port": 22,
  "username": "strg",
  "privateKeyPath": "/secrets/sftp-key"
}
```
