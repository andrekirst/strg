using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Constants;
using Strg.Core.Domain;
using Strg.Core.Storage;
using Strg.Integration.Tests.Upload;

namespace Strg.Integration.Tests.Versioning;

/// <summary>
/// HTTP-level fixture for the STRG-045 file-version-restore endpoint. Inherits
/// <see cref="StrgTusUploadFixture"/>'s PostgreSQL + RabbitMQ + local-FS container shape,
/// and adds a plaintext (non-encrypted) drive plus direct seeders for
/// <see cref="FileItem"/> + <see cref="FileVersion"/> rows backed by real on-disk blobs.
///
/// <para>Why a plaintext drive: the STRG-045 spec models restore as a literal
/// <c>provider.ReadAsync</c> → <c>provider.WriteAsync</c> stream copy with no
/// re-encryption hop. On the encrypted drive the base fixture seeds, that copy would move
/// ciphertext envelopes around without re-keying, leaving the new version unreadable. The
/// dedicated plaintext drive keeps the test scope aligned with the spec; encryption-aware
/// restore is its own follow-up tracker.</para>
///
/// <para>Why direct DB+blob seeding instead of going through the TUS pipeline: each test
/// needs precise control over the seeded version contents (TC-001 must seed exactly two
/// versions with distinct bytes), and the TUS path fires a <c>FileUploadedEvent</c> per
/// upload that would pollute the audit-row assertion in TC-003. Seeding directly on the
/// plaintext drive sidesteps both.</para>
/// </summary>
public sealed class FileVersionRestoreEndpointFixture : StrgTusUploadFixture
{
    public Guid PlainDriveId { get; private set; }
    public string PlainTempRoot { get; private set; } = string.Empty;

    /// <summary>
    /// Seeds a non-encrypted local-FS drive on the same fixture user/tenant. Idempotent on
    /// re-call (returns the already-seeded id). Called once per test class via the first
    /// seeded test.
    /// </summary>
    public async Task SeedPlaintextDriveAsync()
    {
        if (PlainDriveId != Guid.Empty)
        {
            return;
        }

        PlainTempRoot = Directory.CreateTempSubdirectory($"strg-restore-plain-{Guid.NewGuid():N}").FullName;

        await using var ctx = NewDbContext();
        var drive = new Drive
        {
            TenantId = TenantId,
            Name = $"plain-restore-{Guid.NewGuid():N}".ToLowerInvariant(),
            ProviderType = "local",
            ProviderConfig = JsonSerializer.Serialize(new { rootPath = PlainTempRoot }),
            EncryptionEnabled = false,
        };
        ctx.Drives.Add(drive);
        await ctx.SaveChangesAsync();
        PlainDriveId = drive.Id;
    }

    /// <summary>
    /// Seeds a <see cref="FileItem"/> with one initial <see cref="FileVersion"/> on the
    /// plaintext drive. Returns the file id so subsequent <see cref="AddVersionAsync"/> calls
    /// can append further versions. The blob is written to the canonical
    /// <see cref="StrgUploadKeys.FinalKey"/> path so the production read path locates it.
    /// </summary>
    public async Task<Guid> SeedFileWithInitialVersionAsync(
        byte[] plaintext,
        string filename = "doc.txt",
        string mimeType = "application/octet-stream",
        CancellationToken cancellationToken = default)
    {
        if (PlainDriveId == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"Plaintext drive not seeded. Call {nameof(SeedPlaintextDriveAsync)} first.");
        }

        using var scope = Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IStorageProviderRegistry>();

        await using var driveCtx = NewDbContext();
        var drive = await driveCtx.Drives.FirstAsync(d => d.Id == PlainDriveId, cancellationToken);
        var providerConfig = DictionaryStorageProviderConfig.FromJson(drive.ProviderConfig);
        var provider = registry.Resolve(drive.ProviderType, providerConfig);

        var fileItemId = Guid.NewGuid();
        var storageKey = StrgUploadKeys.FinalKey(PlainDriveId, fileItemId, versionNumber: 1);

        await using (var src = new MemoryStream(plaintext))
        {
            await provider.WriteAsync(storageKey, src, cancellationToken);
        }

        var contentHash = ComputeHash(plaintext);

        await using var seedCtx = NewDbContext();
        var fileItem = new FileItem
        {
            Id = fileItemId,
            TenantId = TenantId,
            DriveId = PlainDriveId,
            Name = filename,
            Path = filename,
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
            BlobSizeBytes = plaintext.LongLength,
            ContentHash = contentHash,
            StorageKey = storageKey,
            CreatedBy = UserId,
        };
        seedCtx.FileVersions.Add(version);

        await seedCtx.SaveChangesAsync(cancellationToken);
        return fileItemId;
    }

    /// <summary>
    /// Appends a new <see cref="FileVersion"/> to <paramref name="fileId"/> with the given
    /// plaintext. Updates <see cref="FileItem.Size"/> / <see cref="FileItem.ContentHash"/>
    /// / <see cref="FileItem.StorageKey"/> / <see cref="FileItem.VersionCount"/> to the new
    /// values, mirroring what production's <see cref="Strg.Core.Services.IFileVersionStore.CreateVersionAsync"/>
    /// would do. Bypasses quota commit because the test fixture's owner has a generous quota
    /// and the assertion target is restore semantics, not quota arithmetic.
    /// </summary>
    public async Task AddVersionAsync(
        Guid fileId,
        byte[] plaintext,
        CancellationToken cancellationToken = default)
    {
        using var scope = Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IStorageProviderRegistry>();

        await using var ctx = NewDbContext();
        var drive = await ctx.Drives.FirstAsync(d => d.Id == PlainDriveId, cancellationToken);
        var file = await ctx.Files.FirstAsync(f => f.Id == fileId, cancellationToken);
        var maxVersion = await ctx.FileVersions
            .Where(v => v.FileId == fileId)
            .MaxAsync(v => (int?)v.VersionNumber, cancellationToken) ?? 0;
        var nextVersion = maxVersion + 1;

        var providerConfig = DictionaryStorageProviderConfig.FromJson(drive.ProviderConfig);
        var provider = registry.Resolve(drive.ProviderType, providerConfig);
        var storageKey = StrgUploadKeys.FinalKey(PlainDriveId, fileId, nextVersion);

        await using (var src = new MemoryStream(plaintext))
        {
            await provider.WriteAsync(storageKey, src, cancellationToken);
        }

        var contentHash = ComputeHash(plaintext);

        ctx.FileVersions.Add(new FileVersion
        {
            FileId = fileId,
            VersionNumber = nextVersion,
            Size = plaintext.LongLength,
            BlobSizeBytes = plaintext.LongLength,
            ContentHash = contentHash,
            StorageKey = storageKey,
            CreatedBy = UserId,
        });

        file.Size = plaintext.LongLength;
        file.ContentHash = contentHash;
        file.StorageKey = storageKey;
        file.VersionCount = nextVersion;

        await ctx.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Reads a stored version's blob bytes directly via the provider. The integration test uses
    /// this to assert the restored version's bytes match the source version's bytes
    /// (and that the historical versions' bytes remain accessible via their own keys).
    /// </summary>
    public async Task<byte[]> ReadVersionBytesAsync(
        Guid fileId,
        int versionNumber,
        CancellationToken cancellationToken = default)
    {
        using var scope = Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IStorageProviderRegistry>();

        await using var ctx = NewDbContext();
        var drive = await ctx.Drives.FirstAsync(d => d.Id == PlainDriveId, cancellationToken);
        var version = await ctx.FileVersions
            .FirstAsync(v => v.FileId == fileId && v.VersionNumber == versionNumber, cancellationToken);

        var providerConfig = DictionaryStorageProviderConfig.FromJson(drive.ProviderConfig);
        var provider = registry.Resolve(drive.ProviderType, providerConfig);

        await using var stream = await provider.ReadAsync(version.StorageKey, offset: 0, cancellationToken);
        await using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return ms.ToArray();
    }

    /// <summary>
    /// Counts <see cref="FileVersion"/> rows for the given file. The integration test uses
    /// this to pin the "no version records deleted" code-review checklist invariant from
    /// the issue.
    /// </summary>
    public async Task<int> CountVersionsAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        await using var ctx = NewDbContext();
        return await ctx.FileVersions.CountAsync(v => v.FileId == fileId, cancellationToken);
    }

    /// <summary>Reads the FileItem with global query filters disabled — used when a test needs to
    /// observe post-restore <see cref="FileItem.VersionCount"/> + <see cref="FileItem.Size"/>
    /// without race-y caching from an earlier scope.</summary>
    public async Task<FileItem> ReloadFileAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        await using var ctx = NewDbContext();
        return await ctx.Files.FirstAsync(f => f.Id == fileId, cancellationToken);
    }

    /// <summary>POSTs the password grant with a caller-supplied scope list — needed to exercise
    /// the 403 path when the token lacks <c>files.write</c>.</summary>
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

    private static string ComputeHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
