namespace Strg.Core.Storage;

public interface IStorageItem
{
    string Name { get; }
    string Path { get; }
    bool IsDirectory { get; }
    DateTimeOffset CreatedAt { get; }
    DateTimeOffset UpdatedAt { get; }
}

public interface IStorageFile : IStorageItem
{
    long Size { get; }
    string? ContentHash { get; }
}

public interface IStorageDirectory : IStorageItem { }
