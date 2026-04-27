using System.Net.Mime;

namespace Strg.Core.Domain;

public sealed class FileItem : TenantedEntity
{
    // Mutable (set, not init) to support STRG-040 cross-drive move: the move endpoint
    // re-binds an existing FileItem row to a new drive within the same tenant. The
    // tenant-isolation invariant remains intact via TenantId being init-only on
    // TenantedEntity — cross-drive moves are an intra-tenant rebind, not a cross-tenant
    // one. Without this the endpoint would be forced to soft-delete-then-recreate, which
    // would break the issue's "200 OK with updated FileItem" semantics (the same row id
    // must remain reachable post-move).
    public Guid DriveId { get; set; }
    public Guid? ParentId { get; set; }
    public required string Name { get; set; }
    public required string Path { get; set; }
    public long Size { get; set; }
    public string? ContentHash { get; set; }
    public bool IsDirectory { get; init; }
    public bool IsFolder => IsDirectory;
    public string? StorageKey { get; set; }
    public Guid CreatedBy { get; init; }
    public string MimeType { get; set; } = MediaTypeNames.Application.Octet;
    public int VersionCount { get; set; } = 1;

    // Inbox fields (STRG-305 will add more, keeping these placeholders minimal)
    public bool IsInInbox { get; set; }
    public DateTimeOffset? InboxEnteredAt { get; set; }
}
