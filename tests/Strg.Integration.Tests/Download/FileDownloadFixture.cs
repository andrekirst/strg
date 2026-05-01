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

namespace Strg.Integration.Tests.Download;

/// <summary>
/// HTTP-level fixture for the STRG-037 download endpoint. Inherits
/// <see cref="StrgTusUploadFixture"/>'s PostgreSQL + RabbitMQ container shape (one container
/// per test class, per <c>project_phase12_decisions.md</c>) and its encrypted-drive seed; adds
/// helpers that bypass the TUS HTTP path to directly seed
/// <see cref="FileItem"/> + <see cref="FileVersion"/> + <see cref="FileKey"/> rows alongside a
/// matching ciphertext (or plaintext) blob.
///
/// <para>Why bypass TUS: the download tests need precise control over the seeded shape
/// (specific plaintext bytes, specific MIME type, encrypted vs unencrypted drive) and don't
/// gain coverage from re-running the upload pipeline for every scenario. The seed path uses
/// the same <see cref="IEncryptingFileWriter"/> / <see cref="IStorageProvider"/> as
/// production, so the round-trip still exercises the real crypto envelope.</para>
/// </summary>
public class FileDownloadFixture : StrgTusUploadFixture
{
    public Guid UnencryptedDriveId { get; private set; }
    public string UnencryptedTempRoot { get; private set; } = string.Empty;

    /// <summary>
    /// Adds a sibling <see cref="Drive"/> with <see cref="Drive.EncryptionEnabled"/>=false on a
    /// fresh temp directory, so download tests can compare the encrypted and plaintext read
    /// paths within the same fixture (same auth, same containers).
    /// </summary>
    public async Task SeedUnencryptedDriveAsync()
    {
        if (UnencryptedDriveId != Guid.Empty)
        {
            return;
        }

        UnencryptedTempRoot = Directory.CreateTempSubdirectory($"strg-dl-plain-{Guid.NewGuid():N}").FullName;

        await using var ctx = NewDbContext();
        var drive = new Drive
        {
            TenantId = TenantId,
            Name = $"plain-drive-{Guid.NewGuid():N}".ToLowerInvariant(),
            ProviderType = "local",
            ProviderConfig = JsonSerializer.Serialize(new { rootPath = UnencryptedTempRoot }),
            EncryptionEnabled = false,
        };
        ctx.Drives.Add(drive);
        await ctx.SaveChangesAsync();
        UnencryptedDriveId = drive.Id;
    }

    /// <summary>
    /// Seeds a downloadable encrypted file on <see cref="StrgTusUploadFixture.DriveId"/>. Writes
    /// the ciphertext envelope to the local provider via <see cref="IEncryptingFileWriter"/>
    /// (the same writer the production upload pipeline uses) and persists the matching
    /// FileItem / FileVersion / FileKey rows.
    /// </summary>
    public Task<Guid> SeedEncryptedFileAsync(
        byte[] plaintext,
        string filename = "test.bin",
        string mimeType = "application/octet-stream",
        CancellationToken cancellationToken = default)
        => SeedFileInternalAsync(DriveId, plaintext, filename, mimeType, encrypted: true, cancellationToken);

    /// <summary>
    /// Seeds a downloadable plaintext file on the unencrypted drive (call
    /// <see cref="SeedUnencryptedDriveAsync"/> first).
    /// </summary>
    public Task<Guid> SeedPlaintextFileAsync(
        byte[] plaintext,
        string filename = "test.bin",
        string mimeType = "application/octet-stream",
        CancellationToken cancellationToken = default)
        => SeedFileInternalAsync(UnencryptedDriveId, plaintext, filename, mimeType, encrypted: false, cancellationToken);

    /// <summary>
    /// Seeds a directory entry (FileItem with <see cref="FileItem.IsDirectory"/>=true) on the
    /// encrypted drive. Used by the 400-on-directory acceptance test.
    /// </summary>
    public async Task<Guid> SeedDirectoryAsync(string name = "subfolder", CancellationToken cancellationToken = default)
    {
        await using var ctx = NewDbContext();
        var directory = new FileItem
        {
            TenantId = TenantId,
            DriveId = DriveId,
            Name = name,
            Path = "/" + name,
            Size = 0,
            IsDirectory = true,
            CreatedBy = UserId,
            VersionCount = 0,
        };
        ctx.Files.Add(directory);
        await ctx.SaveChangesAsync(cancellationToken);
        return directory.Id;
    }

    /// <summary>
    /// POSTs the password grant against <c>/connect/token</c> with a caller-supplied scope
    /// list. Used by TC-004 to obtain a token that lacks <c>files.read</c>.
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

    private async Task<Guid> SeedFileInternalAsync(
        Guid driveId,
        byte[] plaintext,
        string filename,
        string mimeType,
        bool encrypted,
        CancellationToken cancellationToken)
    {
        if (driveId == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"Drive id is unset. Call {nameof(SeedUnencryptedDriveAsync)} before SeedPlaintextFileAsync.");
        }

        // Write blob first (outside DB transaction) so a failure here doesn't strand a DB row
        // pointing at an absent storage key. Mirrors the upload pipeline's two-phase order.
        using var scope = Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IStorageProviderRegistry>();
        var encryptingWriterFactory = scope.ServiceProvider.GetRequiredService<IEncryptingFileWriterFactory>();

        await using var dbCtx = NewDbContext();
        var drive = await dbCtx.Drives.FirstAsync(d => d.Id == driveId, cancellationToken);
        var providerConfig = DictionaryStorageProviderConfig.FromJson(drive.ProviderConfig);
        var provider = registry.Resolve(drive.ProviderType, providerConfig);
        // Bind the writer to the per-drive provider — the same pattern FileDownloadResolver
        // uses, so the test seeds bytes against the same provider the SUT reads from.
        var encryptingWriter = encryptingWriterFactory.Create(provider);

        var fileItemId = Guid.NewGuid();
        var storageKey = StrgUploadKeys.FinalKey(driveId, fileItemId, versionNumber: 1);

        long blobSize;
        EncryptedWriteResult? encryptedResult = null;
        if (encrypted)
        {
            using var src = new MemoryStream(plaintext);
            encryptedResult = await encryptingWriter.WriteAsync(
                storageKey,
                src,
                AesGcmFileWriter.AlgorithmName,
                cancellationToken);
            var stored = await provider.GetFileAsync(storageKey, cancellationToken);
            blobSize = stored?.Size ?? 0;
        }
        else
        {
            using var src = new MemoryStream(plaintext);
            await provider.WriteAsync(storageKey, src, cancellationToken);
            blobSize = plaintext.LongLength;
        }

        var contentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(plaintext)).ToLowerInvariant();

        await using var seedCtx = NewDbContext();
        var fileItem = new FileItem
        {
            Id = fileItemId,
            TenantId = TenantId,
            DriveId = driveId,
            Name = filename,
            Path = "/" + filename,
            Size = plaintext.LongLength,
            ContentHash = contentHash,
            IsDirectory = false,
            CreatedBy = UserId,
            MimeType = mimeType,
            VersionCount = 1,
            StorageKey = storageKey,
        };
        seedCtx.Files.Add(fileItem);

        var version = new FileVersion
        {
            FileId = fileItemId,
            VersionNumber = 1,
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
        return fileItemId;
    }

}
