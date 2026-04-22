using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Strg.Core.Domain;
using Strg.Core.Storage;
using Strg.Infrastructure.Data;

namespace Strg.WebDav;

/// <summary>
/// STRG-068 — bridge between the <see cref="StrgWebDavMiddleware"/> and strg's storage domain.
/// Looks up a <see cref="FileItem"/> by (drive, path) in <see cref="StrgDbContext"/> and returns
/// the corresponding <see cref="StrgWebDavCollection"/> (folder) or <see cref="StrgWebDavDocument"/>
/// (file). The drive root itself is a synthetic collection because the drive has no backing
/// <c>FileItem</c>.
///
/// <para><b>Scoped lifetime.</b> Registered scoped in <see cref="WebDavServiceExtensions"/> — the
/// <see cref="StrgDbContext"/> dependency rules out singleton, and a per-request instance mirrors
/// how every other EF-backed service in this codebase is wired.</para>
///
/// <para><b>Tenant isolation.</b> The <see cref="Drive"/> argument has already been resolved
/// through <see cref="IDriveResolver"/> which scopes by <see cref="Infrastructure.Data.ITenantContext.TenantId"/>,
/// so by the time the store runs the drive is guaranteed to belong to the caller. The global
/// query filter on <c>StrgDbContext.Files</c> then re-applies tenant scoping and soft-delete
/// filtering on the FileItem lookup for belt-and-suspenders defence.</para>
/// </summary>
public sealed class StrgWebDavStore(
    StrgDbContext db,
    IStorageProviderRegistry registry,
    ILogger<StrgWebDavStore> logger) : IStrgWebDavStore
{
    public async Task<IStrgWebDavStoreItem?> GetItemAsync(Drive drive, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(path);

        // Empty path = drive root. There is no FileItem for the drive itself; synthesize a root
        // collection whose children are the FileItems with ParentId == null on this drive.
        if (path.Length == 0)
        {
            logger.LogDebug("WebDAV: synthesizing root collection for drive {DriveId}", drive.Id);
            return new StrgWebDavCollection(drive, parent: null, db, registry, this);
        }

        var item = await db.Files
            .AsNoTracking()
            .FirstOrDefaultAsync(
                f => f.DriveId == drive.Id && f.Path == path,
                cancellationToken);

        if (item is null)
        {
            return null;
        }

        return item.IsDirectory
            ? new StrgWebDavCollection(drive, item, db, registry, this)
            : new StrgWebDavDocument(drive, item, registry);
    }

    /// <summary>
    /// Resolves the <see cref="IStorageProvider"/> for <paramref name="drive"/> from the registry.
    /// Shared by collection (for recursive child materialization) and document (for the read
    /// stream), so the JSON config parse happens in one place.
    /// </summary>
    internal static IStorageProvider ResolveProvider(IStorageProviderRegistry registry, Drive drive)
    {
        var config = ParseProviderConfig(drive.ProviderConfig);
        return registry.Resolve(drive.ProviderType, config);
    }

    private static IStorageProviderConfig ParseProviderConfig(string json)
    {
        // Mirrors the JSON shape used by the REST/GraphQL drive-creation surfaces — a flat
        // string-valued map. Empty "{}" is a valid drive config (the built-in "local" provider
        // requires "rootPath" and will throw on its own if missing, which is where operator
        // feedback belongs).
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
        {
            return new DictionaryStorageProviderConfig(new Dictionary<string, string?>());
        }

        var raw = JsonSerializer.Deserialize<Dictionary<string, string?>>(json)
            ?? new Dictionary<string, string?>();
        return new DictionaryStorageProviderConfig(raw);
    }
}

/// <summary>
/// Folder bridge. The <paramref name="parent"/> parameter is <c>null</c> only for the drive-root
/// synthetic collection — distinguishing root from a real folder matters because the children
/// query changes shape (<c>ParentId == null</c> vs <c>ParentId == parent.Id</c>).
/// </summary>
public sealed class StrgWebDavCollection(
    Drive drive,
    FileItem? parent,
    StrgDbContext db,
    IStorageProviderRegistry registry,
    StrgWebDavStore store) : IStrgWebDavStoreCollection
{
    // Null parent ⇒ drive-root collection. Name/Path are drawn from the drive itself so the
    // PROPFIND XML renders the drive name as the root href rather than an empty string.
    public string Name => parent?.Name ?? drive.Name;
    public string Path => parent?.Path ?? string.Empty;
    public DateTimeOffset CreatedAt => parent?.CreatedAt ?? drive.CreatedAt;
    public DateTimeOffset UpdatedAt => parent?.UpdatedAt ?? drive.UpdatedAt;
    public bool IsCollection => true;

    public async IAsyncEnumerable<IStrgWebDavStoreItem> GetChildrenAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // IQueryable → AsAsyncEnumerable is the load-bearing call: it streams rows from the
        // reader instead of materializing the full list. WebDAV clients routinely open folders
        // with tens of thousands of entries; a List<T> here turns every such PROPFIND into an
        // OOM risk. (Spec code-review checklist explicitly pins this.)
        var parentId = parent?.Id;
        var query = db.Files
            .AsNoTracking()
            .Where(f => f.DriveId == drive.Id && f.ParentId == parentId)
            .OrderBy(f => f.Name)
            .AsAsyncEnumerable();

        await foreach (var child in query.WithCancellation(cancellationToken))
        {
            yield return child.IsDirectory
                ? new StrgWebDavCollection(drive, child, db, registry, store)
                : new StrgWebDavDocument(drive, child, registry);
        }
    }

    public Task<int> CountDescendantsBoundedAsync(int limit, CancellationToken cancellationToken = default)
    {
        // Take(limit + 1) ensures Postgres stops after one-past-the-cap; without it every
        // Depth: infinity request would scan the drive's entire file table. The +1 is how the
        // caller discriminates "exactly at cap" (allowed) from "over the cap" (507) — spec's
        // cap semantics are "more than" limit.
        var query = BuildDescendantsQuery();
        return query.Take(limit + 1).CountAsync(cancellationToken);
    }

    public async IAsyncEnumerable<IStrgWebDavStoreItem> GetDescendantsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Same streaming discipline as GetChildrenAsync — no List<T> materialization. The cap
        // check ran upstream in CountDescendantsBoundedAsync; this enumerator only runs after the
        // caller has already verified the result set is under the ceiling.
        var query = BuildDescendantsQuery()
            .OrderBy(f => f.Path)
            .AsAsyncEnumerable();

        await foreach (var descendant in query.WithCancellation(cancellationToken))
        {
            yield return descendant.IsDirectory
                ? new StrgWebDavCollection(drive, descendant, db, registry, store)
                : new StrgWebDavDocument(drive, descendant, registry);
        }
    }

    private IQueryable<FileItem> BuildDescendantsQuery()
    {
        // Drive-root descendants = all FileItems on the drive (tenant + soft-delete filters apply
        // via the global query filter). For a subfolder, descendants match Path.StartsWith(prefix)
        // where prefix is "<parent.Path>/". We exclude the parent itself via the final-segment
        // check — a self-comparison would double-count the collection in the 207 response.
        if (parent is null)
        {
            return db.Files.AsNoTracking().Where(f => f.DriveId == drive.Id);
        }

        var prefix = parent.Path + "/";
        var parentId = parent.Id;
        return db.Files
            .AsNoTracking()
            .Where(f => f.DriveId == drive.Id
                        && f.Id != parentId
                        && f.Path.StartsWith(prefix));
    }
}

/// <summary>
/// File bridge. The underlying <see cref="IStorageProvider.ReadAsync"/> returns a streaming
/// source — <see cref="OpenReadStreamAsync"/> hands it back untouched, which is what keeps
/// multi-GB GETs from blowing the host's heap.
/// </summary>
public sealed class StrgWebDavDocument(
    Drive drive,
    FileItem file,
    IStorageProviderRegistry registry) : IStrgWebDavStoreDocument
{
    public string Name => file.Name;
    public string Path => file.Path;
    public DateTimeOffset CreatedAt => file.CreatedAt;
    public DateTimeOffset UpdatedAt => file.UpdatedAt;
    public bool IsCollection => false;
    public long ContentLength => file.Size;
    public string ContentType => file.MimeType;
    public string? ContentHash => file.ContentHash;
    public int Version => file.VersionCount;

    public Task<Stream> OpenReadStreamAsync(CancellationToken cancellationToken = default)
    {
        // StorageKey may be null if the FileItem row represents a directory-shaped placeholder
        // or a not-yet-uploaded inbox stub; IsDirectory was checked upstream, so reaching here
        // with a null key is a genuine invariant violation — surface it rather than handing the
        // provider a null path.
        var key = file.StorageKey
            ?? throw new InvalidOperationException(
                $"FileItem {file.Id} has no StorageKey — cannot open read stream.");

        // Path.Parse was ALREADY applied during upload; StorageKey is the persisted, trusted
        // form. Re-parsing here would be defensive coding against our own write path.
        var provider = StrgWebDavStore.ResolveProvider(registry, drive);
        return provider.ReadAsync(key, cancellationToken: cancellationToken);
    }
}
