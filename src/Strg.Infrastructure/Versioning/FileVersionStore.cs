using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Core.Services;
using Strg.Core.Storage;
using Strg.Infrastructure.Data;

namespace Strg.Infrastructure.Versioning;

/// <summary>
/// EF Core-backed <see cref="IFileVersionStore"/>. Owns the transaction boundary for the
/// version-create and version-prune operations because both touch multiple tables plus quota
/// state, and rolling them back partially would desync the DB from storage.
///
/// <para><b>Why a store, not a repo?</b> Per CLAUDE.md, repositories never call
/// <c>SaveChangesAsync</c>. But "insert FileVersion row + charge quota + update FileItem counter"
/// is a single atomic unit from the domain's perspective — splitting responsibility across
/// three repos and the caller means every upload path has to replicate the transaction
/// boilerplate. Concentrating that here keeps upload handlers focused on transport concerns.</para>
/// </summary>
public sealed class FileVersionStore(
    StrgDbContext db,
    IFileVersionRepository versionRepo,
    IFileRepository fileRepo,
    IDriveRepository driveRepo,
    IStorageProviderRegistry providerRegistry,
    IQuotaService quotaService) : IFileVersionStore
{
    public async Task<FileVersion> CreateVersionAsync(
        FileItem file,
        string storageKey,
        string contentHash,
        long size,
        long blobSizeBytes,
        Guid createdBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        ArgumentOutOfRangeException.ThrowIfNegative(blobSizeBytes);

        // MAX + 1 on the (FileId, VersionNumber) unique index — if two concurrent uploads ever
        // race to the same file the unique index wins and one of them throws on SaveChangesAsync,
        // at which point the ambient transaction rolls back including the quota commit. The
        // caller sees a constraint violation and retries (or the upload service serializes).
        var previous = await db.FileVersions
            .Where(v => v.FileId == file.Id)
            .MaxAsync(v => (int?)v.VersionNumber, cancellationToken)
            .ConfigureAwait(false);
        var nextNumber = (previous ?? 0) + 1;

        var version = new FileVersion
        {
            FileId = file.Id,
            VersionNumber = nextNumber,
            Size = size,
            BlobSizeBytes = blobSizeBytes,
            ContentHash = contentHash,
            StorageKey = storageKey,
            CreatedBy = createdBy,
        };

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await versionRepo.AddAsync(version, cancellationToken).ConfigureAwait(false);

        file.VersionCount = nextNumber;
        file.Size = size;
        file.ContentHash = contentHash;
        file.StorageKey = storageKey;
        await fileRepo.UpdateAsync(file, cancellationToken).ConfigureAwait(false);

        // Commit quota BEFORE SaveChanges: ExecuteUpdateAsync enlists in the transaction and a
        // QuotaExceededException short-circuits before any row is inserted. Reverse order would
        // insert the FileVersion first and then throw — same rollback outcome, but the DB has
        // pointlessly held row locks for longer.
        await quotaService.CommitAsync(file.CreatedBy, size, cancellationToken).ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return version;
    }

    public Task<IReadOnlyList<FileVersion>> GetVersionsAsync(Guid fileId, CancellationToken cancellationToken = default)
        => versionRepo.ListAsync(fileId, cancellationToken);

    public async Task<FileVersion?> GetVersionAsync(Guid fileId, int versionNumber, CancellationToken cancellationToken = default)
    {
        // Tenant gate: resolve the owning FileItem through the tenant-filtered repo first. Without
        // this, a caller in tenant A could probe version-number existence in tenant B by guessing
        // (fileId, versionNumber) pairs — FileVersion itself is not tenanted.
        var file = await fileRepo.GetByIdAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            return null;
        }

        return await versionRepo.GetAsync(fileId, versionNumber, cancellationToken).ConfigureAwait(false);
    }

    public async Task PruneVersionsAsync(Guid fileId, int keepCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keepCount);

        // keepCount == 0 means "retention disabled — keep everything". Documenting this as a
        // guard rather than an `if (keepCount == 0) prune all` is how we honour the
        // `{"mode":"none"}` default policy without forcing every caller to branch on it.
        if (keepCount == 0)
        {
            return;
        }

        var file = await fileRepo.GetByIdAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            // Cross-tenant lookup or already-deleted file. Nothing to prune; silently return so
            // the upload path's post-commit prune call doesn't throw on a concurrent file delete.
            return;
        }

        var drive = await driveRepo.GetByIdAsync(file.DriveId, cancellationToken).ConfigureAwait(false);
        if (drive is null)
        {
            return;
        }

        var versions = await versionRepo.ListAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (versions.Count <= keepCount)
        {
            return;
        }

        // ListAsync returns newest-first, so Skip(keepCount) lands on the oldest rows to prune.
        var toPrune = versions.Skip(keepCount).ToList();

        var provider = ResolveProvider(drive);

        // Per-version atomic scope (STRG-043 M1). Each iteration is its own unit: blob delete →
        // open tx → DB row remove + quota release → commit. Mid-loop failure at iteration k leaves
        // a crisp "iterations [0..k-1] fully committed, iteration k not attempted or rolled back,
        // iterations [k+1..] untouched" state.
        //
        // The previous shape (delete ALL blobs, then one big DB tx) had the inverse failure mode:
        // a transient provider error mid-loop left storage partially gone and the DB still pointing
        // at vanished blobs, with quota inflation lasting until a successful retry. N small
        // transactions cost more than one large one, but for realistic N in [1..10] the overhead
        // is negligible next to the semantic clarity.
        //
        // Gap semantics: since toPrune is ordered newest-of-old → very-oldest, mid-loop failure
        // produces a middle gap in VersionNumber (e.g., {1,2,3,4,5,8,9,10}). The read path tolerates
        // gaps — GetVersionsAsync returns whatever rows exist — and retry resumes by re-pruning
        // from the same "beyond keepCount" tail.
        foreach (var version in toPrune)
        {
            await provider.DeleteAsync(version.StorageKey, cancellationToken).ConfigureAwait(false);

            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            db.FileVersions.Remove(version);
            await quotaService.ReleaseAsync(file.CreatedBy, version.Size, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Parses the drive's <c>ProviderConfig</c> JSON (flat string→string map in v0.1) into a
    /// <see cref="DictionaryStorageProviderConfig"/> and resolves the provider. Kept private
    /// because <see cref="Drive.ProviderConfig"/> is an internal JSON shape — lifting this helper
    /// out would imply it's a stable public contract, which it isn't yet.
    /// </summary>
    private IStorageProvider ResolveProvider(Drive drive)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(drive.ProviderConfig);
        foreach (var property in json.RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Null => null,
                _ => property.Value.GetRawText(),
            };
        }

        var config = new DictionaryStorageProviderConfig(values);
        return providerRegistry.Resolve(drive.ProviderType, config);
    }
}
