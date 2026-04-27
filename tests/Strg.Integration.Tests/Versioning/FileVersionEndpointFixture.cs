using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Domain;
using Strg.Core.Storage;
using Strg.Integration.Tests.Upload;

namespace Strg.Integration.Tests.Versioning;

/// <summary>
/// HTTP-level fixture for the STRG-044 versions list / content endpoints. Inherits
/// <see cref="StrgTusUploadFixture"/>'s PostgreSQL + RabbitMQ container shape (one container
/// per test class) and seeds an UNENCRYPTED drive — the STRG-044 content endpoint streams
/// raw provider bytes via <c>provider.ReadAsync</c> per the issue spec, so an encrypted drive
/// would surface AES-GCM envelope bytes (not the test plaintext) and the equality assertions
/// in TC-002 would fail. Plaintext-roundtrip is the contract STRG-044 pins; encryption-aware
/// per-version download is a follow-up tracker, not this issue.
///
/// <para>The fixture additionally provides a direct seeder
/// (<see cref="SeedFileWithVersionsAsync"/>) that bypasses the TUS pipeline so version-history
/// tests can create N versions deterministically without paying for N upload round-trips. Each
/// version writes its plaintext to the local provider at the same storage-key shape the
/// upload pipeline uses, so the round-trip covers the same read path production exercises.</para>
/// </summary>
public sealed class FileVersionEndpointFixture : StrgTusUploadFixture
{
    public Guid PlainDriveId { get; private set; }
    private string _plainTempRoot = string.Empty;

    /// <summary>
    /// Creates the unencrypted drive backing the version-content tests. Idempotent: subsequent
    /// calls are a no-op so individual tests do not have to coordinate setup ordering.
    /// </summary>
    public async Task SeedPlainDriveAsync()
    {
        if (PlainDriveId != Guid.Empty)
        {
            return;
        }

        _plainTempRoot = Directory.CreateTempSubdirectory($"strg-fv-plain-{Guid.NewGuid():N}").FullName;

        await using var ctx = NewDbContext();
        var drive = new Drive
        {
            TenantId = TenantId,
            Name = $"plain-versions-drive-{Guid.NewGuid():N}".ToLowerInvariant(),
            ProviderType = "local",
            ProviderConfig = JsonSerializer.Serialize(new { rootPath = _plainTempRoot }),
            EncryptionEnabled = false,
        };
        ctx.Drives.Add(drive);
        await ctx.SaveChangesAsync();
        PlainDriveId = drive.Id;
    }

    /// <summary>
    /// Seeds a <see cref="FileItem"/> on <see cref="PlainDriveId"/> together with N
    /// <see cref="FileVersion"/> rows whose payloads are <paramref name="versionPayloads"/>
    /// (index 0 → version 1, etc.). The latest version's bytes / hash / size become the
    /// FileItem's current values to match production's "FileItem.* tracks the latest version"
    /// contract from <c>FileVersionStore.CreateVersionAsync</c>. Returns the file id.
    ///
    /// <para>Storage keys are deterministic: <c>versions/{fileId}/v{N}</c>. Each blob is written
    /// directly via the registered <see cref="IStorageProvider"/> for the plain drive — same
    /// provider the SUT will read from, so byte equality holds end-to-end.</para>
    /// </summary>
    public async Task<Guid> SeedFileWithVersionsAsync(
        IReadOnlyList<byte[]> versionPayloads,
        string filename = "doc.bin",
        string mimeType = "application/octet-stream",
        CancellationToken cancellationToken = default)
    {
        if (PlainDriveId == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"Plain drive not seeded. Call {nameof(SeedPlainDriveAsync)} first.");
        }
        if (versionPayloads.Count == 0)
        {
            throw new ArgumentException("At least one version payload is required.", nameof(versionPayloads));
        }

        using var scope = Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IStorageProviderRegistry>();

        await using var driveCtx = NewDbContext();
        var drive = await driveCtx.Drives.FirstAsync(d => d.Id == PlainDriveId, cancellationToken);
        var providerConfig = DictionaryStorageProviderConfig.FromJson(drive.ProviderConfig);
        var provider = registry.Resolve(drive.ProviderType, providerConfig);

        var fileId = Guid.NewGuid();

        // Write blobs first (outside the DB transaction). Failure here doesn't strand DB rows;
        // the caller observes the storage exception and the test fails cleanly.
        var versionMetadata = new List<(int Number, byte[] Payload, string StorageKey, string Hash)>();
        for (var i = 0; i < versionPayloads.Count; i++)
        {
            var versionNumber = i + 1;
            var payload = versionPayloads[i];
            var storageKey = $"versions/{fileId:N}/v{versionNumber}";

            using var src = new MemoryStream(payload);
            await provider.WriteAsync(storageKey, src, cancellationToken);

            var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            versionMetadata.Add((versionNumber, payload, storageKey, hash));
        }

        var latest = versionMetadata[^1];

        await using var seedCtx = NewDbContext();
        var fileItem = new FileItem
        {
            Id = fileId,
            TenantId = TenantId,
            DriveId = PlainDriveId,
            Name = filename,
            Path = "/" + filename,
            Size = latest.Payload.LongLength,
            ContentHash = latest.Hash,
            IsDirectory = false,
            CreatedBy = UserId,
            MimeType = mimeType,
            VersionCount = versionMetadata.Count,
            StorageKey = latest.StorageKey,
        };
        seedCtx.Files.Add(fileItem);

        foreach (var (number, payload, storageKey, hash) in versionMetadata)
        {
            seedCtx.FileVersions.Add(new FileVersion
            {
                FileId = fileId,
                VersionNumber = number,
                Size = payload.LongLength,
                BlobSizeBytes = payload.LongLength,
                ContentHash = hash,
                StorageKey = storageKey,
                CreatedBy = UserId,
            });
        }

        await seedCtx.SaveChangesAsync(cancellationToken);
        return fileId;
    }

    /// <summary>
    /// POSTs the password grant with a caller-supplied scope list. Mirrors
    /// <c>FileListFixture.AuthenticateWithScopesAsync</c> — needed to exercise the 403 path
    /// for callers without <c>files.read</c>.
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
    /// Best-effort cleanup of the per-fixture plain-drive temp root when the host disposes.
    /// The base <see cref="StrgTusUploadFixture"/>'s <c>IAsyncLifetime.DisposeAsync</c> is the
    /// authoritative tear-down hook (it stops the testcontainers); we hook the synchronous
    /// <see cref="IDisposable"/> path the base ultimately invokes via
    /// <c>WebApplicationFactory.Dispose</c>. CI runners reap stranded temp dirs eventually, so
    /// failures swallow.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                if (!string.IsNullOrEmpty(_plainTempRoot) && Directory.Exists(_plainTempRoot))
                {
                    Directory.Delete(_plainTempRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort. Stranded test temp dirs are a disk-pressure annoyance, not a
                // correctness problem.
            }
        }
        base.Dispose(disposing);
    }
}
