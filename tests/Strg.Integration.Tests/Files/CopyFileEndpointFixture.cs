using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Integration.Tests.Upload;

namespace Strg.Integration.Tests.Files;

/// <summary>
/// HTTP-level fixture for the STRG-041 file-copy endpoint. Inherits
/// <see cref="StrgTusUploadFixture"/>'s PostgreSQL + RabbitMQ container shape (one container
/// per test class) and its seeded encrypted local-FS drive.
///
/// <para>We need real bytes on disk to exercise the storage <see cref="Strg.Core.Storage.IStorageProvider.CopyAsync"/>
/// path: a copy of "no source content" returns success vacuously and would mask the
/// "different Id, same content" assertion in TC-001. <see cref="SeedFileWithContentAsync"/>
/// writes the bytes through the registry-resolved provider so the test exercises the same
/// drive/provider plumbing the production endpoint hits.</para>
///
/// <para>Paths are seeded in storage-normalized form (no leading or trailing slash),
/// matching <c>StoragePath.Normalize</c>'s output.</para>
/// </summary>
public sealed class CopyFileEndpointFixture : StrgTusUploadFixture
{
    /// <summary>
    /// Seeds a single <see cref="FileItem"/> on the fixture's drive AND writes the
    /// corresponding bytes through the storage provider. The fixture's drive uses the
    /// LocalFileSystemProvider rooted at <see cref="StrgTusUploadFixture.TempStorageRoot"/>;
    /// a successful seed leaves a real file on disk that <c>provider.CopyAsync</c> in the
    /// endpoint can copy from.
    ///
    /// <para>Note the fixture-inherited drive has <see cref="Drive.EncryptionEnabled"/> = true
    /// in the parent, but encryption is applied at the writer layer
    /// (<c>AesGcmFileWriter</c>) — the raw provider stores ciphertext blobs in production.
    /// For copy tests we want byte-for-byte identity in storage, so we write the raw bytes
    /// directly via <c>provider.WriteAsync</c> (bypassing the encrypting writer). The copy
    /// endpoint also bypasses the encrypting layer (it copies opaque blobs), so this matches
    /// the production behaviour of "copy whatever bytes are at the source key".</para>
    /// </summary>
    public async Task<Guid> SeedFileWithContentAsync(
        string path,
        byte[] content,
        string mimeType = "application/octet-stream",
        Guid? driveId = null,
        CancellationToken cancellationToken = default)
    {
        var name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        var contentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));

        var resolvedDriveId = driveId ?? DriveId;

        await using var ctx = NewDbContext();
        var file = new FileItem
        {
            TenantId = TenantId,
            DriveId = resolvedDriveId,
            Name = name,
            Path = path,
            IsDirectory = false,
            Size = content.Length,
            ContentHash = contentHash,
            MimeType = mimeType,
            StorageKey = path,
            CreatedBy = UserId,
            VersionCount = 1,
        };
        ctx.Files.Add(file);

        var version = new FileVersion
        {
            FileId = file.Id,
            VersionNumber = 1,
            Size = content.Length,
            BlobSizeBytes = content.Length,
            ContentHash = contentHash,
            StorageKey = path,
            CreatedBy = UserId,
        };
        ctx.FileVersions.Add(version);

        await ctx.SaveChangesAsync(cancellationToken);

        // Write actual bytes to the storage backend so the endpoint's provider.CopyAsync has
        // real content to copy. The fixture's drive is "local" → LocalFileSystemProvider rooted
        // at TempStorageRoot.
        var rootPath = ReadDriveRootPath(resolvedDriveId);
        var fullPath = System.IO.Path.Combine(rootPath, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var parentDir = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }
        await File.WriteAllBytesAsync(fullPath, content, cancellationToken);

        return file.Id;
    }

    /// <summary>
    /// Seeds a second <see cref="Drive"/> on the same tenant with its own temp root. Used by
    /// the cross-drive copy tests so a different <c>TargetDriveId</c> resolves to a separate
    /// filesystem root, which proves the registry resolution path picks up the target's own
    /// provider config and not the source's.
    /// </summary>
    public async Task<(Guid DriveId, string RootPath)> SeedSecondDriveAsync(
        bool encryptionEnabled,
        CancellationToken cancellationToken = default)
    {
        var rootPath = Directory.CreateTempSubdirectory($"strg-copy-it-target-{Guid.NewGuid():N}").FullName;

        await using var ctx = NewDbContext();
        var drive = new Drive
        {
            TenantId = TenantId,
            Name = $"copy-target-{Guid.NewGuid():N}".ToLowerInvariant(),
            ProviderType = "local",
            ProviderConfig = JsonSerializer.Serialize(new { rootPath }),
            EncryptionEnabled = encryptionEnabled,
        };
        ctx.Drives.Add(drive);
        await ctx.SaveChangesAsync(cancellationToken);
        return (drive.Id, rootPath);
    }

    /// <summary>
    /// Force-sets the user's <c>UsedBytes</c> to <paramref name="usedBytes"/>. Symmetric with
    /// <see cref="StrgTusUploadFixture.SetUserQuotaAsync"/> but more direct for the
    /// "next copy will exceed" path: pinning UsedBytes is easier than computing a quota that
    /// matches the size we're about to push.
    /// </summary>
    public async Task SetUserUsedBytesAsync(long usedBytes, CancellationToken cancellationToken = default)
    {
        await using var ctx = NewDbContext();
        var u = await ctx.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == UserId, cancellationToken);
        u.UsedBytes = usedBytes;
        await ctx.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the seeded drive's root path from <c>Drive.ProviderConfig</c>. The fixture's
    /// inherited drive uses <see cref="StrgTusUploadFixture.TempStorageRoot"/>; second-drive
    /// seeders track theirs separately.
    /// </summary>
    public string ReadDriveRootPath(Guid driveId)
    {
        if (driveId == DriveId)
        {
            return TempStorageRoot;
        }
        using var ctx = NewDbContext();
        var drive = ctx.Drives.Single(d => d.Id == driveId);
        var doc = JsonDocument.Parse(drive.ProviderConfig);
        return doc.RootElement.GetProperty("rootPath").GetString()!;
    }

    /// <summary>
    /// POSTs the password grant with a caller-supplied scope list. Mirrors
    /// <c>FileDeleteFixture.AuthenticateWithScopesAsync</c> — needed to exercise the 403 path
    /// for callers without <c>files.write</c>.
    /// </summary>
    public async Task<string> AuthenticateWithScopesAsync(string scopes)
    {
        using var client = CreateClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = UserEmail,
            ["password"] = TestPassword,
            ["client_id"] = "strg-default",
            ["scope"] = scopes,
        });
        using var response = await client.PostAsync("/connect/token", form);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("access_token").GetString()!;
    }
}
