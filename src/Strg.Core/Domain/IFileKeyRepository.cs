namespace Strg.Core.Domain;

/// <summary>
/// Persistence contract for <see cref="FileKey"/>. Callers stage <see cref="FileKey"/> rows
/// alongside their owning <see cref="FileVersion"/> within the same <c>SaveChangesAsync</c> —
/// this repository does NOT call <c>SaveChangesAsync</c> itself (CLAUDE.md repository pattern).
///
/// <para><b>Tenant scoping.</b> <see cref="FileKey"/> inherits <see cref="Entity"/>, not
/// <see cref="TenantedEntity"/>; there is no global tenant filter on the <c>file_keys</c> table.
/// The only safe lookup path is via <see cref="FileVersion"/> (which is reached through
/// <see cref="IFileRepository"/>, which IS tenant-filtered). Callers MUST NOT resolve
/// <see cref="FileKey"/> rows from raw <c>FileVersionId</c> values supplied by clients.</para>
/// </summary>
public interface IFileKeyRepository
{
    Task<FileKey?> GetByFileVersionAsync(Guid fileVersionId, CancellationToken cancellationToken = default);

    Task AddAsync(FileKey fileKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages <paramref name="fileKey"/> for deletion in the next <c>SaveChangesAsync</c>.
    /// Used by the cross-drive move handler in the <c>E→P</c> and <c>E→E</c> branches: on an
    /// E→P move the source FileKey row must be dropped (the target plaintext drive has no
    /// FileKey), and on an E→E move the source row is dropped and a fresh-DEK row inserted in
    /// the same <c>SaveChangesAsync</c> so EF Core's DELETE-before-INSERT ordering keeps the
    /// <c>FileVersionId</c> unique-index satisfied without bouncing through the database.
    ///
    /// <para>Repository pattern (CLAUDE.md): this method does NOT call <c>SaveChangesAsync</c>;
    /// the caller commits.</para>
    /// </summary>
    void Remove(FileKey fileKey);
}
