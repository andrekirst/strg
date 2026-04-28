using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Constants;
using Strg.Core.Domain;
using Strg.Core.Storage;
using Strg.Infrastructure.Storage.Encryption;
using Strg.Integration.Tests.Upload;

namespace Strg.Integration.Tests.Movement;

/// <summary>
/// HTTP-level fixture for the STRG-040 file-move endpoint. Inherits
/// <see cref="StrgTusUploadFixture"/>'s PostgreSQL + RabbitMQ container shape (one container per
/// test class) and its seeded encrypted drive; adds direct <see cref="FileItem"/>-seeding so move
/// tests can construct precise rows without running uploads, plus a <see cref="ReadFileAsync"/>
/// helper for the post-move "row at new path" assertion (filter-respecting; soft-deleted moves
/// are not in scope here).
///
/// <para>Paths are seeded in storage-normalized form — no leading slash, no trailing slash, e.g.
/// <c>"docs"</c> for a directory and <c>"docs/sub/notes.txt"</c> for a nested file. Mirrors what
/// <c>StoragePath.Normalize</c> produces in <c>StrgTusStore</c> and the existing
/// <c>FileDeleteFixture</c>, so production-shaped paths drive the prefix logic.</para>
///
/// <para>Phase 2 (cross-drive single-file + within-drive directory) adds bytes-aware seeding via
/// <see cref="SeedFileWithBytesAsync"/> and a target-drive present-bytes assertion via
/// <see cref="AssertStorageKeyAbsentAsync"/>. <see cref="SeedSecondDriveAsync"/> is now
/// parameterised on encryption posture so cross-drive E↔P combinations can be exercised.</para>
/// </summary>
public sealed class FileMoveFixture : StrgTusUploadFixture
{
    /// <summary>
    /// Seeds a single <see cref="FileItem"/> on the fixture's drive (or on a caller-supplied drive
    /// id, for the cross-drive 400 test). <paramref name="path"/> must be in storage-normalized
    /// form (no leading or trailing slash).
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
    /// Reads a <see cref="FileItem"/> via the standard query-filter-respecting path. Returns
    /// <see langword="null"/> when the row is missing, soft-deleted, or in a foreign tenant —
    /// matches the production read path callers would see.
    /// </summary>
    public async Task<FileItem?> ReadFileAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = NewDbContext();
        return await ctx.Files
            .FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);
    }

    /// <summary>
    /// Creates a second <see cref="Drive"/> in the fixture's tenant. Returns its id. Provider
    /// config mirrors the primary drive (own temp root). Encryption posture is caller-controlled
    /// so cross-drive E→E / E→P / P→E combinations can be exercised independently. Default
    /// remains <c>true</c> to preserve TC003's existing rejection-pinning shape.
    /// </summary>
    public async Task<Guid> SeedSecondDriveAsync(
        bool encryptionEnabled = true,
        CancellationToken cancellationToken = default)
    {
        // Each second drive lands on its own temp root so the cross-drive bytes-on-disk
        // assertions can prove a fresh storage key was created on the OTHER provider, not just
        // that the primary provider has both keys.
        var secondRoot = Directory.CreateTempSubdirectory($"strg-move-second-{Guid.NewGuid():N}").FullName;

        await using var ctx = NewDbContext();
        var drive = new Drive
        {
            TenantId = TenantId,
            Name = $"second-drive-{Guid.NewGuid():N}".ToLowerInvariant(),
            ProviderType = "local",
            ProviderConfig = JsonSerializer.Serialize(new { rootPath = secondRoot }),
            EncryptionEnabled = encryptionEnabled,
        };
        ctx.Drives.Add(drive);
        await ctx.SaveChangesAsync(cancellationToken);
        return drive.Id;
    }

    /// <summary>
    /// POSTs the password grant with a caller-supplied scope list. Mirrors
    /// <c>FileDeleteFixture.AuthenticateWithScopesAsync</c> — needed to exercise the 403 path for
    /// callers without <c>files.write</c>.
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
    /// Seeds a <see cref="FileItem"/> + <see cref="FileVersion"/> (+ <see cref="FileKey"/> on
    /// encrypted drives) backed by <em>actual bytes</em> on the drive's provider. Mirrors the
    /// portion of <c>StrgTusStore.FinalizeAsync</c> that runs after the temp-blob assemble: write
    /// envelope through the same <see cref="IEncryptingFileWriter"/> used in production, persist
    /// the wrapped DEK, link the rows. Used by Phase-2 cross-drive tests that need to prove the
    /// post-move bytes round-trip back to the original plaintext.
    /// </summary>
    /// <param name="path">Storage-normalized path (no leading or trailing slash).</param>
    /// <param name="plaintext">Raw bytes to seed.</param>
    /// <param name="encrypted">If <c>true</c>, write through the encrypting writer and link a FileKey row.</param>
    /// <param name="driveId">Target drive (defaults to the fixture's primary <see cref="StrgTusUploadFixture.DriveId"/>).</param>
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
    /// Asserts that <paramref name="storageKey"/> is absent from <paramref name="drive"/>'s
    /// provider. Used by the cross-drive bytes-relocation tests to prove the source key was
    /// reaped after the move (modulo best-effort failure semantics). Returns the assertion
    /// outcome via FluentAssertions; throws on mismatch so the test fails at the call site.
    /// </summary>
    public async Task AssertStorageKeyAbsentAsync(
        Drive drive,
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        using var scope = Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IStorageProviderRegistry>();
        var providerConfig = DictionaryStorageProviderConfig.FromJson(drive.ProviderConfig);
        var provider = registry.Resolve(drive.ProviderType, providerConfig);
        var exists = await provider.ExistsAsync(storageKey, cancellationToken);
        if (exists)
        {
            throw new Xunit.Sdk.XunitException(
                $"Expected storage key '{storageKey}' to be absent on drive '{drive.Id}', but it exists.");
        }
    }

    /// <summary>
    /// Reads a <see cref="Drive"/> row directly via a fresh DbContext, bypassing query filters
    /// only when the test explicitly asks for that ergonomic. Filter-respecting by default.
    /// </summary>
    public async Task<Drive?> ReadDriveAsync(Guid driveId, CancellationToken cancellationToken = default)
    {
        await using var ctx = NewDbContext();
        return await ctx.Drives.FirstOrDefaultAsync(d => d.Id == driveId, cancellationToken);
    }

    /// <summary>
    /// GETs the byte payload from the file-download endpoint. Used by Phase-2 cross-drive tests
    /// to prove the post-move bytes round-trip back to the original plaintext. Caller passes the
    /// post-move (driveId, fileId) — the endpoint resolves the per-drive provider and decrypts
    /// against the migrated FileKey row.
    /// </summary>
    public async Task<byte[]> ReadFileBytesAsync(Guid driveId, Guid fileId, string accessToken)
    {
        using var client = CreateAuthenticatedClient(accessToken);
        using var response = await client.GetAsync($"/api/v1/drives/{driveId}/files/{fileId}/content");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }
}
