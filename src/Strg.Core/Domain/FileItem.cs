using System.Net.Mime;

namespace Strg.Core.Domain;

public sealed class FileItem : TenantedEntity
{
    private Guid _driveId;

    /// <summary>
    /// Owning drive id. The accessor pair is intentional:
    /// <list type="bullet">
    ///   <item><c>init</c> permits the canonical object-initializer creation pattern
    ///   (<c>new FileItem { DriveId = ... }</c>) used by every <c>FileItem</c>-creating handler
    ///   in the codebase (<c>StrgTusStore</c>, <c>CreateFolderHandler</c>, <c>FileMutations.CopyFileAsync</c>,
    ///   <c>StrgWebDavStore</c>) — keeping initialization through the property avoids touching
    ///   every creation site.</item>
    ///   <item><see cref="MoveTo"/> mutates the backing field <c>_driveId</c> directly so move
    ///   semantics can flip the drive in lockstep with <see cref="Path"/> and <see cref="Name"/>
    ///   without exposing a public/internal setter that any other call site could write to.
    ///   This preserves CLAUDE.md's "no mutable entities outside the aggregate" prohibition —
    ///   the setter remains <c>init</c>-only from the type-system's perspective, and the only
    ///   in-class mutation site is <see cref="MoveTo"/>'s field write.</item>
    /// </list>
    /// </summary>
    public Guid DriveId
    {
        get => _driveId;
        init => _driveId = value;
    }
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

    /// <summary>
    /// User-scoped tags attached to this file. EF inverse navigation of <see cref="Tag.FileId"/>.
    /// The <c>StrgDbContext</c> "TagUser" named query filter scopes this collection to the current
    /// user — accessing it through EF (LINQ, <c>Include</c>, Hot Chocolate <c>UseFiltering</c>'s
    /// <c>tags.some</c> traversal) only ever returns rows where <c>t.UserId == ICurrentUser.UserId</c>.
    /// </summary>
    public ICollection<Tag> Tags { get; init; } = new List<Tag>();

    /// <summary>
    /// Atomically retargets this file to a new <paramref name="newDriveId"/>, <paramref name="newPath"/>,
    /// and <paramref name="newName"/>. The three fields move in lockstep so a partial mutation can never
    /// leave the entity in a contradictory state (e.g. <c>Path = "archive/x.pdf"</c> with
    /// <c>Name = "y.pdf"</c>). Caller is responsible for transactional persistence
    /// (<see cref="TenantedEntity.UpdatedAt"/> is bumped via the EF SaveChanges interceptor).
    ///
    /// <para>The <c>init</c>-with-backing-field pattern on <see cref="DriveId"/> means this method
    /// is the only call site inside the assembly that can flip the drive after construction.
    /// Outside-aggregate writes are forbidden by CLAUDE.md's "mutable entities outside the
    /// aggregate" rule — concentrating the rewrite here is the type-system enforcement of that
    /// rule.</para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Any of the three arguments is empty (Guid.Empty for the drive id, empty/whitespace for the
    /// path or name). The defensive throws sit at the domain edge so a programming error in a future
    /// caller surfaces at the move site, not via a downstream FK violation or a 404 on the next read.
    /// </exception>
    public void MoveTo(Guid newDriveId, string newPath, string newName)
    {
        if (newDriveId == Guid.Empty)
        {
            throw new ArgumentException("Drive id must not be empty.", nameof(newDriveId));
        }
        if (string.IsNullOrWhiteSpace(newPath))
        {
            throw new ArgumentException("Path must not be empty or whitespace.", nameof(newPath));
        }
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("Name must not be empty or whitespace.", nameof(newName));
        }

        _driveId = newDriveId;
        Path = newPath;
        Name = newName;
    }

    /// <summary>
    /// Rebases this descendant <see cref="FileItem"/> under a renamed/relocated directory root.
    /// The leaf segment is preserved (<see cref="Name"/> is invariant under directory rebase) —
    /// only <see cref="Path"/> and <see cref="DriveId"/> flip. Used exclusively by
    /// <c>MoveFileHandler</c> for descendant rewrites under a directory move.
    ///
    /// <para><b>Asymmetric with <see cref="MoveTo"/>:</b> <see cref="Name"/> is preserved because
    /// the leaf segment is invariant under directory rebase — only the prefix path moves.
    /// Throws <see cref="InvalidOperationException"/> (not <see cref="ArgumentException"/>) on a
    /// non-descendant <see cref="Path"/> because that's a state error, not an argument-shape
    /// error: the caller's arguments may be valid, but THIS row doesn't belong under the old
    /// root.</para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="newDriveId"/> is <see cref="Guid.Empty"/>, or <paramref name="oldRootPath"/>
    /// or <paramref name="newRootPath"/> is empty/whitespace.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Path"/> does not start with <paramref name="oldRootPath"/> + <c>"/"</c> —
    /// signals a programming error (caller passed a non-descendant row).
    /// </exception>
    public void RebaseUnder(string oldRootPath, string newRootPath, Guid newDriveId)
    {
        if (newDriveId == Guid.Empty)
        {
            throw new ArgumentException("Drive id must not be empty.", nameof(newDriveId));
        }
        if (string.IsNullOrWhiteSpace(oldRootPath))
        {
            throw new ArgumentException("Old root path must not be empty or whitespace.", nameof(oldRootPath));
        }
        if (string.IsNullOrWhiteSpace(newRootPath))
        {
            throw new ArgumentException("New root path must not be empty or whitespace.", nameof(newRootPath));
        }

        var anchor = oldRootPath + "/";
        if (!Path.StartsWith(anchor, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"FileItem at '{Path}' is not a descendant of '{oldRootPath}'.");
        }

        _driveId = newDriveId;
        Path = newRootPath + "/" + Path[anchor.Length..];
        // Name unchanged — leaf segment is invariant under directory rebase.
    }
}
