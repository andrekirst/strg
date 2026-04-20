namespace Strg.Core.Domain;

public sealed class FileVersion : Entity
{
    public Guid FileId { get; init; }
    public int VersionNumber { get; init; }
    public long Size { get; init; }
    public required string ContentHash { get; init; }
    public required string StorageKey { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public Guid CreatedBy { get; init; }
}
