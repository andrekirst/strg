using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Constants;
using Strg.Core.Domain;
using Strg.Plugin.Abstractions.Storage;
using Strg.Plugin.Abstractions.Internal.Encryption;
using Strg.Infrastructure.Storage.Encryption;
using Strg.Integration.Tests.Upload;

namespace Strg.Integration.Tests.FileVersions;

/// <summary>
/// HTTP-level fixture for the STRG-044 file-versions endpoints. Inherits
/// <see cref="StrgTusUploadFixture"/>'s PostgreSQL + RabbitMQ container shape and its encrypted
/// drive. Adds <see cref="SeedEncryptedFileWithVersionsAsync"/>, which writes N independently-
/// keyed encrypted blobs and persists <see cref="FileItem"/> + N <see cref="FileVersion"/> +
/// N <see cref="FileKey"/> rows in a single transaction.
///
/// <para>The seed bypasses the TUS upload flow on purpose: the version-list and version-
/// content endpoints are pure reads, so re-running the upload pipeline for each test scenario
/// adds wall-clock cost without exercising additional STRG-044 behavior. The seed uses the
/// same <see cref="IEncryptingFileWriter"/> production code path so the round-trip still
/// exercises the real envelope.</para>
/// </summary>
public class FileVersionFixture : StrgTusUploadFixture
{
    /// <summary>
    /// Seeds one <see cref="FileItem"/> plus one <see cref="FileVersion"/> row per element of
    /// <paramref name="plaintexts"/>. Versions are written in array order and numbered 1..N;
    /// <see cref="FileItem.VersionCount"/>, <see cref="FileItem.Size"/>, and
    /// <see cref="FileItem.ContentHash"/> are pinned to the latest. Each version blob is
    /// encrypted with its own DEK (the production-path STRG-026 invariant: one key per
    /// version).
    /// </summary>
    public async Task<Guid> SeedEncryptedFileWithVersionsAsync(
        byte[][] plaintexts,
        string filename = "versioned.bin",
        string mimeType = "application/octet-stream",
        CancellationToken cancellationToken = default)
    {
        if (plaintexts is null || plaintexts.Length == 0)
        {
            throw new ArgumentException("At least one plaintext version is required.", nameof(plaintexts));
        }

        using var scope = Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IStorageProviderRegistry>();
        var encryptingWriterFactory = scope.ServiceProvider.GetRequiredService<IEncryptingFileWriterFactory>();

        await using var dbCtx = NewDbContext();
        var drive = await dbCtx.Drives.FirstAsync(d => d.Id == DriveId, cancellationToken);
        var providerConfig = DictionaryStorageProviderConfig.FromJson(drive.ProviderConfig);
        var provider = registry.Resolve(drive.ProviderType, providerConfig);
        var encryptingWriter = encryptingWriterFactory.Create(provider);

        var fileItemId = Guid.NewGuid();

        var versionRows = new List<FileVersion>(plaintexts.Length);
        var keyRows = new List<FileKey>(plaintexts.Length);
        string? lastContentHash = null;
        long lastSize = 0;
        string? lastStorageKey = null;

        for (var i = 0; i < plaintexts.Length; i++)
        {
            var versionNumber = i + 1;
            var plaintext = plaintexts[i];
            var storageKey = StrgUploadKeys.FinalKey(DriveId, fileItemId, versionNumber);

            using var src = new MemoryStream(plaintext);
            var encryptedResult = await encryptingWriter.WriteAsync(
                storageKey,
                src,
                AesGcmFileWriter.AlgorithmName,
                cancellationToken);
            var stored = await provider.GetFileAsync(storageKey, cancellationToken);
            var blobSize = stored?.Size ?? 0;

            var contentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(plaintext)).ToLowerInvariant();

            var version = new FileVersion
            {
                FileId = fileItemId,
                VersionNumber = versionNumber,
                Size = plaintext.LongLength,
                BlobSizeBytes = blobSize,
                ContentHash = contentHash,
                StorageKey = storageKey,
                CreatedBy = UserId,
            };
            versionRows.Add(version);
            keyRows.Add(new FileKey
            {
                FileVersionId = version.Id,
                EncryptedDek = encryptedResult.WrappedDek,
                Algorithm = encryptedResult.Algorithm,
            });

            lastContentHash = contentHash;
            lastSize = plaintext.LongLength;
            lastStorageKey = storageKey;
        }

        await using var seedCtx = NewDbContext();
        var fileItem = new FileItem
        {
            Id = fileItemId,
            TenantId = TenantId,
            DriveId = DriveId,
            Name = filename,
            Path = "/" + filename,
            Size = lastSize,
            ContentHash = lastContentHash,
            IsDirectory = false,
            CreatedBy = UserId,
            MimeType = mimeType,
            VersionCount = plaintexts.Length,
            StorageKey = lastStorageKey,
        };
        seedCtx.Files.Add(fileItem);
        seedCtx.FileVersions.AddRange(versionRows);
        seedCtx.FileKeys.AddRange(keyRows);
        await seedCtx.SaveChangesAsync(cancellationToken);

        return fileItemId;
    }

    /// <summary>
    /// POSTs the password grant against <c>/connect/token</c> with a caller-supplied scope list.
    /// Used by the auth-scope test to obtain a token that lacks <c>files.read</c>.
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
