using Strg.Core.Domain;

namespace Strg.WebDav;

/// <summary>
/// STRG-068 — bridge from a resolved <see cref="Drive"/> + request path to an <see cref="IStrgWebDavStoreItem"/>
/// backed by <see cref="Core.Storage.IStorageProvider"/>. The contract intentionally keeps NWebDav
/// out of the signature surface: NWebDav 0.1.x ships an abandoned <c>IHttpContext</c> abstraction
/// and a transitively vulnerable ASP.NET Core adapter (GHSA-hxrm-9w7p-39cc on the
/// <c>NWebDav.Server.AspNetCore</c> side), so STRG-068 takes only the <c>NWebDav.Server</c> core
/// package as a forward-looking dependency and exposes its own interfaces to the middleware.
///
/// <para><b>Path contract.</b> <paramref name="path"/> is the in-drive resource path — everything
/// after <c>/dav/{driveName}</c> — and MUST have been validated through
/// <see cref="Core.Storage.StoragePath.Parse"/> before reaching an implementation. The
/// path-traversal defence is the caller's job; the store treats <paramref name="path"/> as trusted.
/// </para>
/// </summary>
public interface IStrgWebDavStore
{
    /// <summary>
    /// Resolves the FileItem at <paramref name="path"/> on <paramref name="drive"/> to a file
    /// or folder bridge, or <c>null</c> if no item exists (or it is soft-deleted). Returns a
    /// collection when the target is the drive root itself (empty <paramref name="path"/>).
    /// </summary>
    Task<IStrgWebDavStoreItem?> GetItemAsync(Drive drive, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// STRG-070 — PUT handler. Writes <paramref name="content"/> to the blob store, computes its
    /// SHA-256 in-flight, and persists (or updates) the <see cref="FileItem"/> + <see cref="FileVersion"/>
    /// rows. <paramref name="path"/> MUST already be through <see cref="Core.Storage.StoragePath.Parse"/>
    /// (the middleware runs <c>WebDavUriParser.ExtractValidatedPath</c> before reaching here, so the
    /// path-traversal defence is a caller obligation, not a store one).
    ///
    /// <para><b>Semantics.</b>
    /// <list type="bullet">
    ///   <item><description>Returns <c>(document, created: true)</c> when no prior file existed —
    ///     middleware maps to <c>201 Created</c>.</description></item>
    ///   <item><description>Returns <c>(document, created: false)</c> on overwrite — middleware
    ///     maps to <c>204 No Content</c>, and a new <see cref="FileVersion"/> row is appended
    ///     while the FileItem's <c>StorageKey</c> flips to the fresh blob.</description></item>
    ///   <item><description>Throws <see cref="Core.Exceptions.QuotaExceededException"/> if the
    ///     Commit-first gate rejects the uploaded size — the written blob is cleaned up before the
    ///     exception propagates so an over-quota PUT doesn't orphan storage.</description></item>
    ///   <item><description>Throws <see cref="InvalidOperationException"/> if <paramref name="path"/>
    ///     resolves to a folder (PUT on a collection is RFC 4918 §9.7 undefined; middleware maps to
    ///     <c>409 Conflict</c>) or if the parent path doesn't exist.</description></item>
    /// </list>
    /// </para>
    ///
    /// <para><b>Quota order (STRG-032 Commit-first).</b> The blob is written first, then the
    /// atomic <see cref="Core.Services.IQuotaService.CommitAsync"/> charges the user for the
    /// actually-written byte count (which is the only value trustworthy enough to charge — HTTP
    /// <c>Content-Length</c> is client-supplied and chunked encoding can omit it entirely). The
    /// spec's check-then-commit sketch is wrong for the same reason CheckAsync is advisory:
    /// another upload can drain the budget between Check and Commit. On Commit failure the blob
    /// is deleted before the exception surfaces — the storage provider's idempotent Delete
    /// contract (STRG-043) is what makes this safe under retries.</para>
    /// </summary>
    Task<(IStrgWebDavStoreDocument Document, bool Created)> PutDocumentAsync(
        Drive drive,
        string path,
        Stream content,
        string? contentType,
        Guid userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Base contract for any bridged WebDAV item — shared by files and folders. Mirrors the shape
/// the spec sketched for <c>IWebDavStoreItem</c> / <c>IWebDavStoreCollection</c> / <c>IWebDavStoreDocument</c>.
/// </summary>
public interface IStrgWebDavStoreItem
{
    /// <summary>Leaf name (no path separators).</summary>
    string Name { get; }

    /// <summary>In-drive path relative to drive root; empty string for the drive root collection.</summary>
    string Path { get; }

    /// <summary>Creation timestamp of the underlying FileItem.</summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>Last-modified timestamp of the underlying FileItem.</summary>
    DateTimeOffset UpdatedAt { get; }

    /// <summary><c>true</c> for collections (folders), <c>false</c> for documents (files).</summary>
    bool IsCollection { get; }
}

/// <summary>
/// A folder — either the drive root or any intermediate directory. Children enumerate FileItems
/// whose <c>ParentId</c> equals this folder's id (or, for the synthetic drive-root collection,
/// whose <c>ParentId</c> is <c>null</c>).
/// </summary>
public interface IStrgWebDavStoreCollection : IStrgWebDavStoreItem
{
    /// <summary>
    /// Streams child items. Implementations MUST NOT materialize the full result into a
    /// <see cref="List{T}"/> — WebDAV clients (macOS Finder, Windows Explorer) open folders with
    /// tens of thousands of entries and buffering would turn every such PROPFIND into an OOM risk.
    /// </summary>
    IAsyncEnumerable<IStrgWebDavStoreItem> GetChildrenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// STRG-069 — counts descendants (all items nested at any depth under this collection) up to
    /// <paramref name="limit"/> + 1 and returns that bounded count. Backed by a
    /// <c>Take(limit + 1).CountAsync()</c> query so PostgreSQL stops scanning the moment the
    /// ceiling is reached; no <c>SELECT COUNT(*)</c> against a million-row drive.
    ///
    /// <para>The <c>+1</c> is what lets the caller distinguish "at the cap" from "over the cap"
    /// without a second query.</para>
    /// </summary>
    Task<int> CountDescendantsBoundedAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// STRG-069 — streams all descendants (every item at any nesting level under this collection)
    /// for a <c>Depth: infinity</c> PROPFIND. Same streaming discipline as
    /// <see cref="GetChildrenAsync"/>: the implementation MUST NOT buffer — a drive with the cap's
    /// 10 000 items would otherwise hold ~10 000 <c>FileItem</c> objects in memory per concurrent
    /// request.
    /// </summary>
    IAsyncEnumerable<IStrgWebDavStoreItem> GetDescendantsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A file — bridges to <see cref="Core.Storage.IStorageProvider.ReadAsync"/> for the GET body.
/// The stream surface is load-bearing: a <see cref="MemoryStream"/> copy here would defeat
/// <c>CLAUDE.md</c>'s "never buffer large files in memory" rule the moment a client requests a
/// multi-GB file.
/// </summary>
public interface IStrgWebDavStoreDocument : IStrgWebDavStoreItem
{
    /// <summary>File size in bytes (plaintext denominator per STRG-026 #5).</summary>
    long ContentLength { get; }

    /// <summary>Content-Type for the HTTP response.</summary>
    string ContentType { get; }

    /// <summary>
    /// Content hash of the blob (<c>sha256:...</c>). Surfaced as both the quoted <c>getetag</c>
    /// response property (per HTTP spec) and the custom <c>strg:contenthash</c> dead property so
    /// deduplicating clients can rely on it without parsing the quoted ETag shape. <c>null</c> for
    /// files whose upload never completed the post-commit hash pass — those render without an ETag.
    /// </summary>
    string? ContentHash { get; }

    /// <summary>
    /// Current version number for the <c>strg:version</c> custom property. Mirrors
    /// <see cref="Core.Domain.FileItem.VersionCount"/>, which is incremented by
    /// <see cref="Core.Storage.IFileVersionStore"/> on each write. Exposed so clients that support
    /// versioned conflict resolution can see the live version without a separate GraphQL query.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Opens a read stream for the underlying blob. Caller disposes. No buffering — the stream
    /// is the provider's own stream (possibly chunked-GCM decrypted for encrypted drives).
    /// </summary>
    Task<Stream> OpenReadStreamAsync(CancellationToken cancellationToken = default);
}
