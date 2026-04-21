namespace Strg.Core.Storage;

/// <summary>
/// Common metadata for any item (file or directory) returned by an <see cref="IStorageProvider"/>.
/// </summary>
public interface IStorageItem
{
    /// <summary>The leaf name (no path separators).</summary>
    string Name { get; }

    /// <summary>The full path relative to the drive root, separated by '/'.</summary>
    string Path { get; }

    /// <summary><c>true</c> for directories; <c>false</c> for files.</summary>
    bool IsDirectory { get; }

    /// <summary>Time the item was created on the storage backend.</summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>Time the item was last modified on the storage backend.</summary>
    DateTimeOffset UpdatedAt { get; }
}

/// <summary>
/// File metadata returned by an <see cref="IStorageProvider"/>.
/// </summary>
public interface IStorageFile : IStorageItem
{
    /// <summary>Size of the file in bytes.</summary>
    long Size { get; }

    /// <summary>SHA-256 hex hash of the file content, or <c>null</c> if not yet computed.</summary>
    string? ContentHash { get; }
}

/// <summary>
/// Directory metadata returned by an <see cref="IStorageProvider"/>.
/// </summary>
public interface IStorageDirectory : IStorageItem { }
