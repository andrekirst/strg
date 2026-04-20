namespace Strg.Core.Domain;

public sealed class FileItem : TenantedEntity
{
    public Guid DriveId { get; init; }
    public Guid? ParentId { get; set; }
    public required string Name { get; set; }
    public required string Path { get; set; }
    public long Size { get; set; }
    public string? ContentHash { get; set; }
    public bool IsDirectory { get; init; }
    public Guid CreatedBy { get; init; }
    public string MimeType { get; set; } = "application/octet-stream";
    public int VersionCount { get; set; } = 1;

    // Inbox fields (STRG-305 will add more, keeping these placeholders minimal)
    public bool IsInInbox { get; set; }
    public DateTimeOffset? InboxEnteredAt { get; set; }
}
