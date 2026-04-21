using Strg.Core.Storage;

namespace Strg.Infrastructure.Storage;

/// <summary>
/// <see cref="IStorageFile"/> snapshot backed by a <see cref="FileInfo"/>. Values are captured
/// eagerly at construction time so the record is safe to pass around after the underlying file
/// has moved or been deleted (matches the "metadata by value" contract of IStorageFile).
/// <para>
/// <see cref="IStorageFile.ContentHash"/> is intentionally left <c>null</c>: content hashing is a
/// separate cross-cutting concern (STRG-034) owned by the upload pipeline, not the raw file
/// provider. Computing it here would force a full stream read on every <c>GetFileAsync</c>.
/// </para>
/// </summary>
internal sealed record LocalStorageFile(
    string Name,
    string Path,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Size) : IStorageFile
{
    public bool IsDirectory => false;
    public string? ContentHash => null;

    public static LocalStorageFile FromFileInfo(string relativePath, FileInfo info) => new(
        Name: info.Name,
        Path: relativePath,
        CreatedAt: info.CreationTimeUtc,
        UpdatedAt: info.LastWriteTimeUtc,
        Size: info.Length);
}

/// <summary>
/// <see cref="IStorageDirectory"/> snapshot backed by a <see cref="DirectoryInfo"/>. Mirrors the
/// by-value semantics of <see cref="LocalStorageFile"/>.
/// </summary>
internal sealed record LocalStorageDirectory(
    string Name,
    string Path,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt) : IStorageDirectory
{
    public bool IsDirectory => true;

    public static LocalStorageDirectory FromDirectoryInfo(string relativePath, DirectoryInfo info) => new(
        Name: info.Name,
        Path: relativePath,
        CreatedAt: info.CreationTimeUtc,
        UpdatedAt: info.LastWriteTimeUtc);
}
