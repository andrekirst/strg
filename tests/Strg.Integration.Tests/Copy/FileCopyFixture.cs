using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Constants;
using Strg.Core.Domain;
using Strg.Plugin.Abstractions.Storage;
using Strg.Plugin.Abstractions.Storage.Encryption;
using Strg.Infrastructure.Storage.Encryption;
using Strg.Integration.Tests.Upload;

namespace Strg.Integration.Tests.Copy;

/// <summary>
/// HTTP-level fixture for the STRG-041 file-copy endpoint. Inherits
/// <see cref="StrgTusUploadFixture"/>'s PostgreSQL + RabbitMQ container shape (one container per
/// test class) and its seeded encrypted drive; mirrors <c>FileMoveFixture</c>'s helpers
/// (<see cref="SeedFileAsync"/>, <see cref="SeedFileWithBytesAsync"/>,
/// <see cref="SeedSecondDriveAsync"/>) so the cross-encryption test matrix is identical to the
/// move suite — copy and move share the byte-relocation primitive, so the same seeding shapes
/// drive both.
///
/// <para>Paths are seeded in storage-normalized form — no leading slash, no trailing slash, e.g.
/// <c>"docs/source.txt"</c>. Mirrors what <c>StoragePath.Normalize</c> produces in production.</para>
/// </summary>
public sealed class FileCopyFixture : StrgTusUploadFixture
{
    /// <summary>
    /// Seeds a single <see cref="FileItem"/> on the fixture's drive (metadata-only — no bytes).
    /// Used for negative-path tests where bytes don't matter (404, 409, validation).
    /// </summary>
    public async Task<Guid> SeedFileAsync(
        string path,
        bool isDirectory = false,
        string? mimeType = null,
        long size = 0,
        Guid? driveId = null,
        CancellationToken cancellationToken = default)
    {
        var name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;

        await using var ctx = NewDbContext();
        var file = new FileItem
        {
            TenantId = TenantId,
            DriveId = driveId ?? DriveId,
            Name = name,
            Path = path,
            IsDirectory = isDirectory,
            Size = size,
            MimeType = mimeType ?? (isDirectory ? "inode/directory" : "application/octet-stream"),
            CreatedBy = UserId,
            VersionCount = isDirectory ? 0 : 1,
        };
        ctx.Files.Add(file);
        await ctx.SaveChangesAsync(cancellationToken);
        return file.Id;
    }

    /// <summary>
    /// Seeds a <see cref="FileItem"/> + <see cref="FileVersion"/> (+ <see cref="FileKey"/> on
    /// encrypted drives) backed by <em>actual bytes</em> on the drive's provider. Mirrors
    /// <c>FileMoveFixture.SeedFileWithBytesAsync</c>'s shape so the cross-encryption matrix for
    /// copy is identical to the move suite's.
    /// </summary>
    public async Task<Guid> SeedFileWithBytesAsync(
        string path,
        byte[] plaintext,
        bool encrypted,
        Guid? driveId = null,
        CancellationToken cancellationToken = default)
    {
        var actualDriveId = driveId ?? DriveId;

        using var scope = Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IStorageProviderRegistry>();
        var encryptingWriterFactory = scope.ServiceProvider.GetRequiredService<IEncryptingFileWriterFactory>();

        await using var dbCtx = NewDbContext();
        var drive = await dbCtx.Drives.FirstAsync(d => d.Id == actualDriveId, cancellationToken);
        var providerConfig = DictionaryStorageProviderConfig.FromJson(drive.ProviderConfig);
        var provider = registry.Resolve(drive.ProviderType, providerConfig);

        var fileId = Guid.NewGuid();
        const int versionNumber = 1;
        var storageKey = StrgUploadKeys.FinalKey(actualDriveId, fileId, versionNumber);

        long blobSize;
        EncryptedWriteResult? encryptedResult = null;
        if (encrypted)
        {
            using var src = new MemoryStream(plaintext);
            encryptedResult = await encryptingWriterFactory
                .Create(provider)
                .WriteAsync(storageKey, src, AesGcmFileWriter.AlgorithmName, cancellationToken);
            var stored = await provider.GetFileAsync(storageKey, cancellationToken);
            blobSize = stored?.Size ?? plaintext.LongLength;
        }
        else
        {
            using var src = new MemoryStream(plaintext);
            await provider.WriteAsync(storageKey, src, cancellationToken);
            blobSize = plaintext.LongLength;
        }

        var contentHash = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();
        var name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;

        await using var seedCtx = NewDbContext();
        var fileItem = new FileItem
        {
            Id = fileId,
            TenantId = TenantId,
            DriveId = actualDriveId,
            Name = name,
            Path = path,
            Size = plaintext.LongLength,
            ContentHash = contentHash,
            IsDirectory = false,
            CreatedBy = UserId,
            MimeType = "application/octet-stream",
            VersionCount = versionNumber,
            StorageKey = storageKey,
        };
        seedCtx.Files.Add(fileItem);

        var version = new FileVersion
        {
            FileId = fileId,
            VersionNumber = versionNumber,
            Size = plaintext.LongLength,
            BlobSizeBytes = blobSize,
            ContentHash = contentHash,
            StorageKey = storageKey,
            CreatedBy = UserId,
        };
        seedCtx.FileVersions.Add(version);

        if (encrypted && encryptedResult is not null)
        {
            seedCtx.FileKeys.Add(new FileKey
            {
                FileVersionId = version.Id,
                EncryptedDek = encryptedResult.WrappedDek,
                Algorithm = encryptedResult.Algorithm,
            });
        }

        await seedCtx.SaveChangesAsync(cancellationToken);
        return fileId;
    }

    /// <summary>
    /// Creates a second <see cref="Drive"/> in the fixture's tenant. Encryption posture is
    /// caller-controlled so cross-drive E↔P combinations can be exercised.
    /// </summary>
    public async Task<Guid> SeedSecondDriveAsync(
        bool encryptionEnabled = true,
        CancellationToken cancellationToken = default)
    {
        var secondRoot = Directory.CreateTempSubdirectory($"strg-copy-second-{Guid.NewGuid():N}").FullName;

        await using var ctx = NewDbContext();
        var drive = new Drive
        {
            TenantId = TenantId,
            Name = $"copy-second-drive-{Guid.NewGuid():N}".ToLowerInvariant(),
            ProviderType = "local",
            ProviderConfig = JsonSerializer.Serialize(new { rootPath = secondRoot }),
            EncryptionEnabled = encryptionEnabled,
        };
        ctx.Drives.Add(drive);
        await ctx.SaveChangesAsync(cancellationToken);
        return drive.Id;
    }

    /// <summary>
    /// Reads a <see cref="FileItem"/> via the standard query-filter-respecting path. Returns
    /// <see langword="null"/> when the row is missing, soft-deleted, or in a foreign tenant.
    /// </summary>
    public async Task<FileItem?> ReadFileAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = NewDbContext();
        return await ctx.Files.FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);
    }

    /// <summary>
    /// POSTs the password grant with a caller-supplied scope list. Needed to exercise the 403
    /// path for callers without <c>files.write</c>.
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

    /// <summary>
    /// GETs the byte payload from the file-download endpoint. Used by cross-drive copy tests to
    /// prove the post-copy bytes round-trip back to the original plaintext through the target
    /// drive's encryption envelope.
    /// </summary>
    public async Task<byte[]> ReadFileBytesAsync(Guid driveId, Guid fileId, string accessToken)
    {
        using var client = CreateAuthenticatedClient(accessToken);
        using var response = await client.GetAsync($"/api/v1/drives/{driveId}/files/{fileId}/content");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }
}
