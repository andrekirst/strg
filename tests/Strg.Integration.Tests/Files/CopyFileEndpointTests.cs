using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Strg.Api.Endpoints;
using Strg.Core.Domain;
using Xunit;

namespace Strg.Integration.Tests.Files;

/// <summary>
/// STRG-041 — REST file-copy endpoint integration tests. Class-scoped fixture
/// (<see cref="CopyFileEndpointFixture"/>) gives one PostgreSQL + RabbitMQ container shared
/// across all test methods. Each test scopes its seeded files under a unique top-level
/// folder so state from earlier tests in the same class can't bleed into later assertions.
/// </summary>
public sealed class CopyFileEndpointTests(CopyFileEndpointFixture fx) : IClassFixture<CopyFileEndpointFixture>
{
    [Fact]
    public async Task TC001_Copy_CreatesNewFile_WithDifferentId_AndSameContent()
    {
        // Per AC: "POST /copy → 201 Created with new FileItem (different Id from source)" and
        // "Copy → new file has different Id, same content". We assert (a) HTTP 201, (b) the new
        // FileItem.Id != source.Id (issue's CR checklist), (c) the storage blob is byte-for-byte
        // identical at the new path, (d) a FileVersion v1 row exists for the new file.
        var folder = $"tc001-{Guid.NewGuid():N}";
        var content = Encoding.UTF8.GetBytes("hello copy world " + Guid.NewGuid().ToString("N"));
        var sourceId = await fx.SeedFileWithContentAsync($"{folder}/source.txt", content);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var request = new CopyFileRequest($"{folder}/copy.txt", null);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/copy",
            request);

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "201 is the AC's stated success surface for the copy endpoint");

        var dto = await response.Content.ReadFromJsonAsync<FileItemDto>();
        dto.Should().NotBeNull();
        dto!.Id.Should().NotBe(sourceId,
            "the new FileItem.Id is a fresh Guid.NewGuid() per the issue's code-review checklist");
        dto.Path.Should().Be($"{folder}/copy.txt");
        dto.Size.Should().Be(content.Length);
        dto.ContentHash.Should().Be(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)));

        // Verify the storage blob landed at the target path with identical content. The fixture's
        // drive uses LocalFileSystemProvider rooted at TempStorageRoot.
        var rootPath = fx.ReadDriveRootPath(fx.DriveId);
        var copiedFile = System.IO.Path.Combine(rootPath, folder, "copy.txt");
        File.Exists(copiedFile).Should().BeTrue("storage provider must have created the new blob");
        var copiedBytes = await File.ReadAllBytesAsync(copiedFile);
        copiedBytes.Should().BeEquivalentTo(content, "copy must be byte-for-byte identical");

        // Verify FileVersion v1 row exists for the new file (issue's CR checklist).
        await using var ctx = fx.NewDbContext();
        var version = await ctx.FileVersions
            .FirstOrDefaultAsync(v => v.FileId == dto.Id);
        version.Should().NotBeNull(
            "the copy endpoint must create a FileVersion for the new file");
        version!.VersionNumber.Should().Be(1,
            "FileVersion.VersionNumber = 1 per the issue's code-review checklist");
        version.Size.Should().Be(content.Length);
    }

    [Fact]
    public async Task TC002_Copy_ToExistingPath_Returns409Conflict()
    {
        // AC: "Copy to existing path → 409 Conflict". We seed both source and target paths up
        // front, then attempt to copy onto the occupied target.
        var folder = $"tc002-{Guid.NewGuid():N}";
        var content = Encoding.UTF8.GetBytes("source content");
        var sourceId = await fx.SeedFileWithContentAsync($"{folder}/source.txt", content);
        await fx.SeedFileWithContentAsync($"{folder}/already-here.txt", Encoding.UTF8.GetBytes("blocker"));

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var request = new CopyFileRequest($"{folder}/already-here.txt", null);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/copy",
            request);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // The target row must not have been replaced — assert the original occupant survives by
        // hash (the same path in the DB before and after the rejected copy).
        await using var ctx = fx.NewDbContext();
        var occupants = await ctx.Files
            .Where(f => f.DriveId == fx.DriveId && f.Path == $"{folder}/already-here.txt")
            .ToListAsync();
        occupants.Should().HaveCount(1, "the existing occupant must still be the only row at that path");
        occupants[0].ContentHash.Should().Be(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("blocker"))),
            "the rejected copy must not have replaced the existing occupant's content");
    }

    [Fact]
    public async Task TC003_Copy_ExceedsQuota_Returns507InsufficientStorage()
    {
        // AC: "Copy exceeds quota → 507 Insufficient Storage". We seed a file the user already
        // owns, then push UsedBytes up so the next copy of source.Size bytes exceeds the quota.
        var folder = $"tc003-{Guid.NewGuid():N}";
        var content = new byte[1024]; // 1 KiB
        new Random(42).NextBytes(content);
        var sourceId = await fx.SeedFileWithContentAsync($"{folder}/source.bin", content);

        // Pin UsedBytes to QuotaBytes so the very next byte exceeds. CheckAsync's Available
        // computation is QuotaBytes - UsedBytes; with these equal, requiredBytes > 0 fails.
        await fx.SetUserUsedBytesAsync(fx.QuotaBytes);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var request = new CopyFileRequest($"{folder}/copy.bin", null);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/copy",
            request);

        response.StatusCode.Should().Be(HttpStatusCode.InsufficientStorage,
            "507 is the AC's stated quota-shortfall surface");

        // The endpoint must not have created a target FileItem when quota was rejected.
        await using var ctx = fx.NewDbContext();
        var targetExists = await ctx.Files
            .AnyAsync(f => f.DriveId == fx.DriveId && f.Path == $"{folder}/copy.bin");
        targetExists.Should().BeFalse(
            "no FileItem may exist at the target path when quota check rejected the copy");

        // Restore UsedBytes for downstream tests in the same class fixture.
        await fx.SetUserUsedBytesAsync(0);
    }

    [Fact]
    public async Task TC004_Copy_LeavesSourceFileUnchanged()
    {
        // AC: "Original file unchanged" + "Copy → new file has different Id, same content".
        // We snapshot the source row and storage blob before the copy, perform a successful
        // copy, then re-snapshot and assert byte-for-byte identity on both.
        var folder = $"tc004-{Guid.NewGuid():N}";
        var content = Encoding.UTF8.GetBytes("immutable source " + Guid.NewGuid().ToString("N"));
        var sourceId = await fx.SeedFileWithContentAsync($"{folder}/source.txt", content);

        // Snapshot source state before copy.
        FileItem sourceBefore;
        await using (var ctx = fx.NewDbContext())
        {
            sourceBefore = await ctx.Files.SingleAsync(f => f.Id == sourceId);
        }
        var rootPath = fx.ReadDriveRootPath(fx.DriveId);
        var sourceBlobPath = System.IO.Path.Combine(rootPath, folder, "source.txt");
        var blobBefore = await File.ReadAllBytesAsync(sourceBlobPath);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var request = new CopyFileRequest($"{folder}/copy.txt", null);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/copy",
            request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        // Re-snapshot source.
        FileItem sourceAfter;
        await using (var ctx = fx.NewDbContext())
        {
            sourceAfter = await ctx.Files.SingleAsync(f => f.Id == sourceId);
        }
        var blobAfter = await File.ReadAllBytesAsync(sourceBlobPath);

        sourceAfter.Path.Should().Be(sourceBefore.Path,
            "the source FileItem.Path must not have shifted");
        sourceAfter.Size.Should().Be(sourceBefore.Size);
        sourceAfter.ContentHash.Should().Be(sourceBefore.ContentHash);
        sourceAfter.StorageKey.Should().Be(sourceBefore.StorageKey);
        sourceAfter.IsDeleted.Should().BeFalse("the source must not be soft-deleted by the copy");
        blobAfter.Should().BeEquivalentTo(blobBefore,
            "the source blob's bytes must be byte-for-byte unchanged");
    }

    [Fact]
    public async Task Copy_PathTraversal_Returns400()
    {
        var folder = $"trav-{Guid.NewGuid():N}";
        var content = Encoding.UTF8.GetBytes("source");
        var sourceId = await fx.SeedFileWithContentAsync($"{folder}/source.txt", content);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        // StoragePath.Parse rejects '..' segments and absolute paths — surface is 400.
        var request = new CopyFileRequest("../../etc/passwd", null);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/copy",
            request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Copy_Unauthenticated_Returns401()
    {
        var folder = $"unauth-{Guid.NewGuid():N}";
        var sourceId = await fx.SeedFileWithContentAsync(
            $"{folder}/src.txt", Encoding.UTF8.GetBytes("x"));

        using var client = fx.CreateClient();
        var request = new CopyFileRequest($"{folder}/dst.txt", null);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/copy",
            request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Copy_WithoutFilesWriteScope_Returns403()
    {
        // RequireAuthorization(AuthPolicies.FilesWrite) gates the route — a token with files.read
        // but no files.write is rejected with 403 before the handler runs.
        var folder = $"scope-{Guid.NewGuid():N}";
        var sourceId = await fx.SeedFileWithContentAsync(
            $"{folder}/src.txt", Encoding.UTF8.GetBytes("x"));

        var token = await fx.AuthenticateWithScopesAsync("files.read files.share");
        using var client = fx.CreateAuthenticatedClient(token);

        var request = new CopyFileRequest($"{folder}/dst.txt", null);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/copy",
            request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Copy_FromWrongDrive_Returns404()
    {
        // Cross-drive id mismatch is collapsed to 404 (NOT 403) so the wire shape cannot
        // enumerate which drive a file belongs to. Same enumeration-oracle stance as
        // FileDeleteHandler.
        var folder = $"wrongdrive-{Guid.NewGuid():N}";
        var sourceId = await fx.SeedFileWithContentAsync(
            $"{folder}/src.txt", Encoding.UTF8.GetBytes("x"));

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var request = new CopyFileRequest($"{folder}/dst.txt", null);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{Guid.NewGuid()}/files/{sourceId}/copy",
            request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Copy_UnknownFile_Returns404()
    {
        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var request = new CopyFileRequest("dst.txt", null);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{Guid.NewGuid()}/copy",
            request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CrossDriveCopy_SameTenant_SameEncryptionPosture_Succeeds()
    {
        // The optional TargetDriveId routes the copy to a different drive on the same tenant.
        // This pins (a) the registry resolves the TARGET drive's provider config (so the
        // target's rootPath, not the source's, owns the new blob) and (b) the new FileItem
        // carries the target's DriveId.
        var folder = $"xdrive-{Guid.NewGuid():N}";
        var content = Encoding.UTF8.GetBytes("cross drive payload " + Guid.NewGuid().ToString("N"));
        var sourceId = await fx.SeedFileWithContentAsync($"{folder}/src.txt", content);

        // Match the fixture's source-drive encryption posture (true) so the cross-encryption
        // guard doesn't 409 us. This test is about ROUTING, not encryption-mismatch handling.
        var (targetDriveId, targetRoot) = await fx.SeedSecondDriveAsync(encryptionEnabled: true);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var request = new CopyFileRequest($"{folder}/dst.txt", targetDriveId);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/copy",
            request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var dto = await response.Content.ReadFromJsonAsync<FileItemDto>();
        dto.Should().NotBeNull();
        dto!.Path.Should().Be($"{folder}/dst.txt");

        await using var ctx = fx.NewDbContext();
        var newRow = await ctx.Files.SingleAsync(f => f.Id == dto.Id);
        newRow.DriveId.Should().Be(targetDriveId,
            "the new FileItem must live on the TARGET drive, not the source drive");

        // Verify the blob landed on the TARGET filesystem root, not the source's.
        var targetBlob = System.IO.Path.Combine(targetRoot, folder, "dst.txt");
        File.Exists(targetBlob).Should().BeTrue(
            "the storage copy must hit the target drive's provider config (rootPath)");
    }
}
