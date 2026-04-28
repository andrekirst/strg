using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Auditing;
using Strg.Core.Constants;
using Strg.Core.Domain;
using Xunit;

namespace Strg.Integration.Tests.Copy;

/// <summary>
/// STRG-041 — REST file-copy endpoint integration tests. Class-scoped fixture
/// (<see cref="FileCopyFixture"/>) gives one PostgreSQL + RabbitMQ container shared across all
/// test methods. Each test scopes its seeded files under a unique top-level folder so state from
/// earlier tests in the same class can't bleed into later assertions.
///
/// <para>Coverage shape: TC001 happy path; TC002 collision; TC003 quota exhaustion (no analog in
/// the move suite — move is byte-neutral); TC004 source untouched; plus extras for cross-drive
/// E↔P combinations, directory rejection, auth, audit-row dispatch, validation, and self-collision.</para>
/// </summary>
public sealed class FileCopyTests(FileCopyFixture fx) : IClassFixture<FileCopyFixture>
{
    [Fact]
    public async Task TC001_CopyFile_Returns201_NewFileItem_DifferentId_SameContent()
    {
        // AC: "POST /copy → 201 Created with new FileItem (different Id from source)"
        // AC: "Original file unchanged" / "New FileVersion created for copied file (version 1)"
        var folder = $"tc001-{Guid.NewGuid():N}";
        var plaintext = System.Text.Encoding.UTF8.GetBytes("tc001-payload-" + Guid.NewGuid());
        var sourceId = await fx.SeedFileWithBytesAsync($"{folder}/source.txt", plaintext, encrypted: true);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var newPath = $"{folder}/copy.txt";
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/copy",
            new { targetPath = newPath, targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();

        var body = await response.Content.ReadFromJsonAsync<CopyFileResponseDto>();
        body.Should().NotBeNull();
        body!.Id.Should().NotBe(sourceId, "AC: copy must produce a fresh Guid.NewGuid()");
        body.DriveId.Should().Be(fx.DriveId);
        body.Path.Should().Be(newPath);
        body.Name.Should().Be("copy.txt");
        body.Size.Should().Be(plaintext.LongLength);

        // Round-trip plaintext through the copy.
        var downloaded = await fx.ReadFileBytesAsync(fx.DriveId, body.Id, token);
        downloaded.Should().Equal(plaintext);

        // FileVersion v1 exists for the copy.
        await using var ctx = fx.NewDbContext();
        var copyVersion = await ctx.FileVersions.SingleAsync(v => v.FileId == body.Id);
        copyVersion.VersionNumber.Should().Be(1, "AC: copies start at version 1");
        copyVersion.ContentHash.Should().NotBeNullOrEmpty();

        // Original FileItem row is untouched.
        var originalAfter = await fx.ReadFileAsync(sourceId);
        originalAfter.Should().NotBeNull();
        originalAfter!.Path.Should().Be($"{folder}/source.txt");
    }

    [Fact]
    public async Task TC002_CopyToOccupiedPath_Returns409_OriginalUnchanged()
    {
        // AC: "Copy to existing path → 409 Conflict"
        var folder = $"tc002-{Guid.NewGuid():N}";
        var sourceId = await fx.SeedFileAsync($"{folder}/source.txt", size: 10);
        var targetPath = $"{folder}/existing.txt";
        await fx.SeedFileAsync(targetPath);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/copy",
            new { targetPath, targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var body = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        body.Should().NotBeNull();
        body!.Code.Should().Be("Conflict");

        var sourceRow = await fx.ReadFileAsync(sourceId);
        sourceRow!.Path.Should().Be($"{folder}/source.txt");
    }

    [Fact]
    public async Task TC003_CopyExceedingQuota_Returns507()
    {
        // AC: "Copy exceeds quota → 507 Insufficient Storage"
        // Set quota to 2.5 KiB and pre-charge UsedBytes to 2048 so a 2 KiB copy lands with only
        // 512 bytes of headroom — Commit's atomic UPDATE returns rowsAffected=0, throws
        // QuotaExceededException, endpoint surfaces 507.
        var folder = $"tc003-{Guid.NewGuid():N}";
        var plaintext = new byte[2 * 1024];
        new Random(42).NextBytes(plaintext);
        var sourceId = await fx.SeedFileWithBytesAsync($"{folder}/big.bin", plaintext, encrypted: true);

        await fx.SetUserQuotaAsync(2560);

        await using (var ctx = fx.NewDbContext())
        {
            var user = await ctx.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == fx.UserId);
            user.UsedBytes = 2048;
            await ctx.SaveChangesAsync();
        }

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/copy",
            new { targetPath = $"{folder}/copy.bin", targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.InsufficientStorage); // 507

        // No new FileItem row should have landed.
        await using var ctx2 = fx.NewDbContext();
        var copies = await ctx2.Files
            .Where(f => f.DriveId == fx.DriveId && f.Path == $"{folder}/copy.bin")
            .CountAsync();
        copies.Should().Be(0, "a 507-rejected copy must leave no FileItem row");

        // UsedBytes should remain at 2048 — Commit threw before the row landed (or compensation
        // released after a post-Commit failure).
        var usedAfter = await fx.ReadUsedBytesAsync();
        usedAfter.Should().Be(2048);

        // Reset quota for subsequent tests in the same class fixture. UsedBytes stays at 2048
        // for the rest of the run; payloads in other tests are far below the 10 MiB headroom.
        await fx.SetUserQuotaAsync(10L * 1024 * 1024);
    }

    [Fact]
    public async Task TC004_OriginalFile_Unchanged_AfterCopy()
    {
        // AC: "Original file unchanged" — explicit pin on byte-level + DB-level invariance.
        var folder = $"tc004-{Guid.NewGuid():N}";
        var plaintext = System.Text.Encoding.UTF8.GetBytes("tc004-original-bytes");
        var sourceId = await fx.SeedFileWithBytesAsync($"{folder}/original.txt", plaintext, encrypted: true);

        // Capture original storage key BEFORE the copy.
        string originalStorageKey;
        await using (var ctx = fx.NewDbContext())
        {
            var v = await ctx.FileVersions.SingleAsync(x => x.FileId == sourceId);
            originalStorageKey = v.StorageKey;
        }

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/copy",
            new { targetPath = $"{folder}/copy.txt", targetDriveId = (Guid?)null });
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        // Original DB row unchanged — Path, Name, Size, ContentHash, StorageKey.
        var originalAfter = await fx.ReadFileAsync(sourceId);
        originalAfter.Should().NotBeNull();
        originalAfter!.Path.Should().Be($"{folder}/original.txt");
        originalAfter.Name.Should().Be("original.txt");
        originalAfter.Size.Should().Be(plaintext.LongLength);
        originalAfter.StorageKey.Should().Be(originalStorageKey);

        // Original bytes still readable via the original drive's provider.
        var originalDownload = await fx.ReadFileBytesAsync(fx.DriveId, sourceId, token);
        originalDownload.Should().Equal(plaintext);
    }

    [Fact]
    public async Task TC005_PathTraversal_Returns400_InvalidPath()
    {
        var folder = $"tc005-{Guid.NewGuid():N}";
        var sourceId = await fx.SeedFileAsync($"{folder}/source.txt");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/copy",
            new { targetPath = "../../etc/passwd", targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        body!.Code.Should().Be("InvalidPath");
    }

    [Fact]
    public async Task TC006_CopyFile_FromWrongDrive_Returns404()
    {
        // Cross-drive id mismatch on SOURCE collapses to 404 (enumeration-oracle protection).
        var folder = $"tc006-{Guid.NewGuid():N}";
        var sourceId = await fx.SeedFileAsync($"{folder}/source.txt");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{Guid.NewGuid()}/files/{sourceId}/copy",
            new { targetPath = $"{folder}/copy.txt", targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TC007_DirectoryCopy_Returns400_DirectoryCopyUnsupported()
    {
        // v1.5 deferral — mirrors STRG-040's CrossDriveDirectoryUnsupported shape.
        var folder = $"tc007-{Guid.NewGuid():N}";
        var dirId = await fx.SeedFileAsync($"{folder}/dir", isDirectory: true);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{dirId}/copy",
            new { targetPath = $"{folder}/dir-copy", targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        body!.Code.Should().Be("DirectoryCopyUnsupported");
    }

    [Fact]
    public async Task TC008_Unauthenticated_Returns401()
    {
        var sourceId = await fx.SeedFileAsync($"tc008-{Guid.NewGuid():N}.txt");

        using var client = fx.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/copy",
            new { targetPath = "anywhere.txt", targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TC009_WithoutFilesWriteScope_Returns403()
    {
        var sourceId = await fx.SeedFileAsync($"tc009-{Guid.NewGuid():N}.txt");

        var token = await fx.AuthenticateWithScopesAsync("files.read files.share");
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/copy",
            new { targetPath = "anywhere.txt", targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TC010_CrossDriveCopy_E_to_E_BytesRoundTrip_FreshDek()
    {
        // Cross-drive E→E: both source and target encrypted. Target gets a fresh DEK; round-trip
        // plaintext through the target drive's encryption envelope.
        var folder = $"tc010-{Guid.NewGuid():N}";
        var plaintext = System.Text.Encoding.UTF8.GetBytes("tc010-secret-" + Guid.NewGuid());
        var sourceId = await fx.SeedFileWithBytesAsync($"{folder}/source.bin", plaintext, encrypted: true);
        var targetDriveId = await fx.SeedSecondDriveAsync(encryptionEnabled: true);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/copy",
            new { targetPath = $"{folder}/copy.bin", targetDriveId });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<CopyFileResponseDto>();
        body!.DriveId.Should().Be(targetDriveId);

        var downloaded = await fx.ReadFileBytesAsync(targetDriveId, body.Id, token);
        downloaded.Should().Equal(plaintext);

        // FileKey row exists for the new file's version with the AES-GCM-256 algorithm.
        await using var ctx = fx.NewDbContext();
        var newVersion = await ctx.FileVersions.SingleAsync(v => v.FileId == body.Id);
        var newKey = await ctx.FileKeys.SingleAsync(k => k.FileVersionId == newVersion.Id);
        newKey.Algorithm.Should().Be(EncryptionAlgorithms.AesGcm256);

        // Source row unchanged.
        (await fx.ReadFileAsync(sourceId))!.Path.Should().Be($"{folder}/source.bin");
    }

    [Fact]
    public async Task TC011_CrossDriveCopy_PlaintextToEncrypted_FileKeyAdded()
    {
        // P→E: source plaintext drive, target encrypted drive. Handler must (a) read plaintext
        // raw, (b) write through encrypting writer, (c) ADD a FileKey row for the target.
        var folder = $"tc011-{Guid.NewGuid():N}";
        var plaintext = System.Text.Encoding.UTF8.GetBytes("tc011-plaintext-" + Guid.NewGuid());
        var plaintextDriveId = await fx.SeedSecondDriveAsync(encryptionEnabled: false);
        var sourceId = await fx.SeedFileWithBytesAsync(
            $"{folder}/plain.bin", plaintext, encrypted: false, driveId: plaintextDriveId);
        var encryptedTargetDriveId = await fx.SeedSecondDriveAsync(encryptionEnabled: true);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{plaintextDriveId}/files/{sourceId}/copy",
            new { targetPath = $"{folder}/copy.bin", targetDriveId = encryptedTargetDriveId });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<CopyFileResponseDto>();
        var downloaded = await fx.ReadFileBytesAsync(encryptedTargetDriveId, body!.Id, token);
        downloaded.Should().Equal(plaintext);

        await using var ctx = fx.NewDbContext();
        var newVersion = await ctx.FileVersions.SingleAsync(v => v.FileId == body.Id);
        (await ctx.FileKeys.AnyAsync(k => k.FileVersionId == newVersion.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task TC012_CrossDriveCopy_EncryptedToPlaintext_NoFileKeyOnTarget()
    {
        // E→P: source encrypted, target plaintext. Handler must (a) decrypt source via source
        // FileKey, (b) write plaintext raw, (c) NOT add a FileKey row for the target.
        var folder = $"tc012-{Guid.NewGuid():N}";
        var plaintext = System.Text.Encoding.UTF8.GetBytes("tc012-payload-" + Guid.NewGuid());
        var sourceId = await fx.SeedFileWithBytesAsync($"{folder}/encrypted.bin", plaintext, encrypted: true);
        var plaintextTargetDriveId = await fx.SeedSecondDriveAsync(encryptionEnabled: false);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/copy",
            new { targetPath = $"{folder}/copy.bin", targetDriveId = plaintextTargetDriveId });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<CopyFileResponseDto>();
        var downloaded = await fx.ReadFileBytesAsync(plaintextTargetDriveId, body!.Id, token);
        downloaded.Should().Equal(plaintext);

        await using var ctx = fx.NewDbContext();
        var newVersion = await ctx.FileVersions.SingleAsync(v => v.FileId == body.Id);
        (await ctx.FileKeys.AnyAsync(k => k.FileVersionId == newVersion.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task TC013_SameDriveSamePath_Returns409_SelfCollision()
    {
        // Self-collision: targetPath equals source.Path on the same drive. GetByPathAsync
        // returns the source row itself; collision check trips → 409. Asymmetric with move —
        // move guards against move-to-self via collision.Id != file.Id, copy must NOT.
        var folder = $"tc013-{Guid.NewGuid():N}";
        var path = $"{folder}/file.txt";
        var sourceId = await fx.SeedFileAsync(path);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/copy",
            new { targetPath = path, targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task TC014_CopyFile_EmitsFileUploadedAuditEntry()
    {
        // Outbox round-trip is asserted via the audit row that AuditLogConsumer writes on
        // FileUploadedEvent. Same 30s polling envelope as FileMoveTests.TC006 — bare query
        // without retry would read pre-dispatch and flake.
        var folder = $"tc014-{Guid.NewGuid():N}";
        var plaintext = System.Text.Encoding.UTF8.GetBytes("tc014");
        var sourceId = await fx.SeedFileWithBytesAsync($"{folder}/source.txt", plaintext, encrypted: true);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/copy",
            new { targetPath = $"{folder}/copy.txt", targetDriveId = (Guid?)null });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<CopyFileResponseDto>();

        AuditEntry? entry = null;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var ctx = fx.NewDbContext();
            entry = await ctx.AuditEntries
                .FirstOrDefaultAsync(e => e.Action == AuditActions.FileUploaded && e.ResourceId == body!.Id);
            if (entry is not null)
            {
                break;
            }
            await Task.Delay(500);
        }

        entry.Should().NotBeNull("FileUploadedEvent must reach AuditLogConsumer via the outbox");
        entry!.UserId.Should().Be(fx.UserId);
        entry.TenantId.Should().Be(fx.TenantId);
        entry.ResourceType.Should().Be(AuditResourceTypes.FileItem);
        entry.Details.Should().Contain($"\"driveId\":\"{fx.DriveId}\"");
    }

    [Fact]
    public async Task TC015_TargetDriveDoesNotExist_Returns404()
    {
        var sourceId = await fx.SeedFileAsync($"tc015-{Guid.NewGuid():N}.txt");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/copy",
            new { targetPath = "anywhere.txt", targetDriveId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed record CopyFileResponseDto(
        Guid Id,
        Guid DriveId,
        string Name,
        string Path,
        long Size,
        string MimeType,
        bool IsDirectory,
        string? ContentHash,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record ErrorResponseDto(string Code, string Message);
}
