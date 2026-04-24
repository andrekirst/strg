using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Strg.Core.Domain;
using Strg.Core.Services;
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
    IQuotaService quotaService,
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

    public async Task<(IStrgWebDavStoreDocument Document, bool Created)> PutDocumentAsync(
        Drive drive,
        string path,
        Stream content,
        string? contentType,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(content);

        if (path.Length == 0)
        {
            // RFC 4918 §9.7.2: PUT on a collection is undefined. Middleware translates to 409.
            throw new InvalidOperationException("PUT on the drive root is not supported.");
        }

        // Parent path is everything before the final '/'. Empty parent ⇒ the new item lives at
        // drive root (ParentId = null). Non-empty parent ⇒ the folder FileItem must already
        // exist; an auto-mkcol would violate RFC 4918 §9.7.1 (the client should MKCOL first).
        var lastSlash = path.LastIndexOf('/');
        var parentPath = lastSlash < 0 ? string.Empty : path[..lastSlash];
        var leafName = lastSlash < 0 ? path : path[(lastSlash + 1)..];

        if (leafName.Length == 0)
        {
            throw new InvalidOperationException($"PUT path ends with a slash: {path}");
        }

        Guid? parentId = null;
        if (parentPath.Length > 0)
        {
            var parentItem = await db.Files
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    f => f.DriveId == drive.Id && f.Path == parentPath && f.IsDirectory,
                    cancellationToken)
                .ConfigureAwait(false);

            if (parentItem is null)
            {
                // RFC 4918 §9.7.1: the server SHOULD return 409 when the parent doesn't exist.
                // WebDAV clients are expected to create directories with MKCOL before uploading.
                throw new InvalidOperationException(
                    $"Parent folder {parentPath} does not exist on drive {drive.Name}.");
            }
            parentId = parentItem.Id;
        }

        var existing = await db.Files
            .FirstOrDefaultAsync(
                f => f.DriveId == drive.Id && f.Path == path,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is { IsDirectory: true })
        {
            // Overwriting a folder with a file would corrupt the hierarchy — refuse. Maps to 409.
            throw new InvalidOperationException(
                $"Path {path} is an existing folder; PUT would replace it with a file.");
        }

        var created = existing is null;
        var provider = ResolveProvider(registry, drive);

        // Each write gets its own opaque storage key so historical FileVersion rows keep pointing
        // at intact blobs — overwriting the previous key in-place would silently destroy v1's
        // content the moment v2 lands. STRG-043 Prune can reclaim stale blobs later.
        var storageKey = $"{drive.Id:N}/{Guid.NewGuid():N}.blob";

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytesWritten;
        string hashHex;

        // Wrap the HTTP request body so the provider reads through us — bytes hash + count as a
        // side-effect of CopyToAsync. leaveInnerOpen: true because ASP.NET Core owns the request
        // body stream lifecycle; disposing it here would corrupt the pipeline.
        await using (var hashStream = new HashingStream(content, hasher, leaveInnerOpen: true))
        {
            try
            {
                await provider.WriteAsync(storageKey, hashStream, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup — any bytes the provider managed to land are orphaned
                // storage if we don't reap them here. CancellationToken.None because the outer
                // token has likely already fired (cancellation is one of the failure paths).
                await TryDeleteBlobAsync(provider, storageKey).ConfigureAwait(false);
                throw;
            }

            bytesWritten = hashStream.BytesRead;
            hashHex = Convert.ToHexString(hashStream.GetHashAndReset()).ToLowerInvariant();
        }

        var mime = string.IsNullOrWhiteSpace(contentType) ? MediaTypeNames.Application.Octet : contentType;

        try
        {
            // Commit-first quota (STRG-032). The atomic UPDATE is the ONLY race-safe budget gate;
            // a pre-write CheckAsync can and will be beaten by a concurrent upload. We charge
            // the actually-written byte count because client-supplied Content-Length can lie
            // (or be absent for chunked encoding).
            await quotaService.CommitAsync(userId, bytesWritten, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Commit failed (most likely QuotaExceededException). Reclaim the blob before the
            // exception propagates so over-quota PUTs don't leak storage.
            await TryDeleteBlobAsync(provider, storageKey).ConfigureAwait(false);
            throw;
        }

        // DB-side transaction: insert/update FileItem + append FileVersion. If this rolls back
        // we'd have charged quota for bytes with no DB pointer — rare (EF insert failures post-
        // quota-commit are either connection loss or unique-constraint collisions under race),
        // so we compensate with ReleaseAsync in the catch.
        FileItem file;
        int nextVersion;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            if (created)
            {
                file = new FileItem
                {
                    TenantId = drive.TenantId,
                    DriveId = drive.Id,
                    ParentId = parentId,
                    Name = leafName,
                    Path = path,
                    Size = bytesWritten,
                    ContentHash = hashHex,
                    MimeType = mime,
                    StorageKey = storageKey,
                    CreatedBy = userId,
                    VersionCount = 1,
                };
                db.Files.Add(file);
                nextVersion = 1;
            }
            else
            {
                file = existing!;
                nextVersion = file.VersionCount + 1;
                file.Size = bytesWritten;
                file.ContentHash = hashHex;
                file.MimeType = mime;
                file.StorageKey = storageKey;
                file.VersionCount = nextVersion;
                db.Files.Update(file);
            }

            // Every write records a FileVersion row (v1 for creates, v(n+1) for overwrites) so
            // STRG-043 Prune has history to act on and clients calling strg:version see a
            // monotonically-increasing number.
            db.FileVersions.Add(new FileVersion
            {
                FileId = file.Id,
                VersionNumber = nextVersion,
                Size = bytesWritten,
                BlobSizeBytes = bytesWritten,
                ContentHash = hashHex,
                StorageKey = storageKey,
                CreatedBy = userId,
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // DB roll-back already happened when the transaction went out of scope without
            // Commit; compensate the quota charge and reap the blob. ReleaseAsync on a missing
            // user is a silent no-op per IQuotaService contract, so double-compensating from a
            // concurrent reaper path can't go negative.
            await TryReleaseQuotaAsync(userId, bytesWritten).ConfigureAwait(false);
            await TryDeleteBlobAsync(provider, storageKey).ConfigureAwait(false);
            throw;
        }

        logger.LogDebug(
            "WebDAV PUT: drive {DriveId} path {Path} bytes {Bytes} created={Created} version={Version}",
            drive.Id, path, bytesWritten, created, nextVersion);

        return (new StrgWebDavDocument(drive, file, registry), created);
    }

    private async Task TryDeleteBlobAsync(IStorageProvider provider, string storageKey)
    {
        try
        {
            await provider.DeleteAsync(storageKey, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Compensating delete is best-effort: the orphan-reaper (STRG-026 #2) is the
            // authoritative sweep. Surfacing this exception would mask the original failure that
            // caused the cleanup path to run.
            logger.LogWarning(ex, "WebDAV PUT: failed to delete orphan blob {StorageKey}", storageKey);
        }
    }

    private async Task TryReleaseQuotaAsync(Guid userId, long bytes)
    {
        try
        {
            await quotaService.ReleaseAsync(userId, bytes, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WebDAV PUT: failed to release quota ({Bytes} bytes) for user {UserId}", bytes, userId);
        }
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
