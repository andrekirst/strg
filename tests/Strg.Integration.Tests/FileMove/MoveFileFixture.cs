using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Integration.Tests.Upload;

namespace Strg.Integration.Tests.FileMove;

/// <summary>
/// HTTP-level fixture for the STRG-040 file-move endpoint. Inherits
/// <see cref="StrgTusUploadFixture"/>'s PostgreSQL + RabbitMQ container shape (one container
/// per test class) and its seeded encrypted local-FS drive; layers a second drive on top
/// (lazy-initialized) for cross-drive moves and adds direct <see cref="FileItem"/>-seeding so
/// tests can construct precise (driveId, path) layouts without round-tripping through the
/// upload pipeline.
///
/// <para>Paths are seeded in storage-normalized form — no leading slash, no trailing slash
/// (e.g. <c>"docs/sub/notes.txt"</c>) — matching what <c>StoragePath.Normalize</c> produces
/// in production. Each test method scopes its files under a unique top-level folder so
/// concurrent / repeated tests cannot collide on the same path.</para>
///
/// <para><b>Why lazy-init for the secondary drive instead of overriding
/// <c>IAsyncLifetime.InitializeAsync</c></b>: the base class implements that method
/// explicitly, so a derived override cannot invoke the base via standard inheritance and a
/// naive cross-cast would recurse infinitely. Lazy <see cref="EnsureSecondaryDriveAsync"/>
/// is allocated once per fixture and ensures the seed happens at most once per test class —
/// idempotent and race-free under concurrent test execution within the class.</para>
/// </summary>
public sealed class MoveFileFixture : StrgTusUploadFixture
{
    private readonly Lazy<Task<(Guid DriveId, string Root)>> _secondaryDriveTask;

    public MoveFileFixture()
    {
        _secondaryDriveTask = new Lazy<Task<(Guid, string)>>(SeedSecondaryDriveAsync);
    }

    /// <summary>
    /// Returns the lazy-initialized secondary drive's id (for the cross-drive move test).
    /// Call <see cref="EnsureSecondaryDriveAsync"/> first if you also need the root path.
    /// </summary>
    public async Task<Guid> GetSecondaryDriveIdAsync()
    {
        var (id, _) = await _secondaryDriveTask.Value;
        return id;
    }

    /// <summary>
    /// Returns the lazy-initialized secondary drive's local-FS root directory. Tests that
    /// also seed file blobs on the secondary drive need both id and root.
    /// </summary>
    public async Task<(Guid DriveId, string Root)> EnsureSecondaryDriveAsync() =>
        await _secondaryDriveTask.Value;

    private async Task<(Guid, string)> SeedSecondaryDriveAsync()
    {
        var root = Directory.CreateTempSubdirectory($"strg-move-secondary-{Guid.NewGuid():N}").FullName;

        await using var ctx = NewDbContext();
        var drive = new Drive
        {
            TenantId = TenantId,
            Name = $"move-secondary-{Guid.NewGuid():N}".ToLowerInvariant(),
            ProviderType = "local",
            ProviderConfig = JsonSerializer.Serialize(new { rootPath = root }),
            EncryptionEnabled = false,
        };
        ctx.Drives.Add(drive);
        await ctx.SaveChangesAsync();
        return (drive.Id, root);
    }

    /// <summary>
    /// Seeds a single <see cref="FileItem"/> on a target drive. <paramref name="path"/> must
    /// be in storage-normalized form. Returns the new file's id so the test can address it
    /// via the move endpoint. The actual blob is also written to the local-FS provider's root
    /// so storage-side <c>MoveAsync</c> has something to relocate; without this the move
    /// endpoint succeeds at the DB level but throws inside the LocalFileSystemProvider on a
    /// missing source.
    /// </summary>
    public async Task<Guid> SeedFileWithBlobAsync(
        Guid driveId,
        string driveRoot,
        string path,
        bool isDirectory = false,
        string? mimeType = null,
        long size = 0,
        byte[]? content = null,
        CancellationToken cancellationToken = default)
    {
        var name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;

        await using var ctx = NewDbContext();
        var file = new FileItem
        {
            TenantId = TenantId,
            DriveId = driveId,
            Name = name,
            Path = path,
            IsDirectory = isDirectory,
            Size = size == 0 && content is not null ? content.Length : size,
            MimeType = mimeType ?? (isDirectory ? "inode/directory" : "application/octet-stream"),
            CreatedBy = UserId,
            VersionCount = isDirectory ? 0 : 1,
        };
        ctx.Files.Add(file);
        await ctx.SaveChangesAsync(cancellationToken);

        // Write the blob (or create the directory) on the drive's local-FS root so the
        // provider's MoveAsync has something to relocate. Without this, the endpoint's
        // post-DB storage move throws FileNotFoundException and the test would surface as 500.
        var fullPath = Path.Combine(driveRoot, path.Replace('/', Path.DirectorySeparatorChar));
        if (!isDirectory)
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            await File.WriteAllBytesAsync(fullPath, content ?? [], cancellationToken);
        }
        else
        {
            Directory.CreateDirectory(fullPath);
        }

        return file.Id;
    }

    /// <summary>
    /// Reads a <see cref="FileItem"/> directly from the DB with global query filters
    /// disabled. Needed for assertions on rows that may have been mutated by the move
    /// endpoint (DriveId rebind, Path rewrite); the global tenant filter is sufficient to
    /// exclude unrelated rows but the soft-delete filter would mask any inadvertent
    /// soft-delete from a future regression.
    /// </summary>
    public async Task<FileItem?> ReadFileBypassingFiltersAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = NewDbContext();
        return await ctx.Files
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);
    }
}
