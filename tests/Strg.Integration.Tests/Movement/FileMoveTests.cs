using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Auditing;
using Strg.Core.Constants;
using Strg.Core.Domain;
using Strg.Integration.Tests.Common;
using Xunit;

namespace Strg.Integration.Tests.Movement;

/// <summary>
/// STRG-040 — REST file-move endpoint integration tests. Class-scoped fixture
/// (<see cref="FileMoveFixture"/>) gives one PostgreSQL + RabbitMQ container shared across all
/// test methods. Each test scopes its seeded files under a unique top-level folder so state from
/// earlier tests in the same class can't bleed into later assertions.
///
/// <para><b>Phase 2 enables.</b> Cross-drive single-file moves (TC003 flipped to success;
/// TC011/TC012/TC013 add bytes-level coverage for E↔E / P→E / E→P), within-drive directory
/// moves with descendant rewrites (TC005 flipped, TC010 covers N=5 plus prefix-anchor sibling
/// safety, TC015 pins the descendant-prefix collision check). Cross-drive directory moves remain
/// rejected with the new <c>CrossDriveDirectoryUnsupported</c> error code (TC014 pins it).</para>
/// </summary>
public sealed class FileMoveTests(FileMoveFixture fx) : IClassFixture<FileMoveFixture>
{
    [Fact]
    public async Task TC001_MoveFile_Returns200_NewPathReachable_OldPathGone()
    {
        var folder = $"tc001-{Guid.NewGuid():N}";
        var fileId = await fx.SeedFileAsync($"{folder}/source.txt");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var newPath = $"{folder}/moved/destination.txt";
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{fileId}/move",
            new { targetPath = newPath, targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<MoveFileResponseDto>();
        body.Should().NotBeNull();
        body!.Id.Should().Be(fileId);
        body.Path.Should().Be(newPath);
        body.Name.Should().Be("destination.txt");
        body.DriveId.Should().Be(fx.DriveId);

        // The DB row reflects the move — "old path returns 404 after move" in AC terms is the
        // same row at the new path; the original Path string is no longer findable.
        var rowAfter = await fx.ReadFileAsync(fileId);
        rowAfter.Should().NotBeNull();
        rowAfter!.Path.Should().Be(newPath);
        rowAfter.Name.Should().Be("destination.txt");
    }

    [Fact]
    public async Task TC002_MoveToOccupiedPath_Returns409()
    {
        var folder = $"tc002-{Guid.NewGuid():N}";
        var sourceId = await fx.SeedFileAsync($"{folder}/source.txt");
        var targetPath = $"{folder}/existing.txt";
        await fx.SeedFileAsync(targetPath);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/move",
            new { targetPath, targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Source file must NOT have been mutated on the conflict path.
        var sourceRow = await fx.ReadFileAsync(sourceId);
        sourceRow.Should().NotBeNull();
        sourceRow!.Path.Should().Be($"{folder}/source.txt");
    }

    [Fact]
    public async Task TC003_CrossDriveMove_SingleFile_Returns200_RowOnTargetDrive()
    {
        // Phase 2 — flipped from rejection-pin to happy-path. Bytes-level verification is in TC011;
        // this test stays at the metadata layer (cheaper, no encryption-write involved). The seed
        // bypasses storage; only the DB-mutation path is exercised end-to-end.
        var folder = $"tc003-{Guid.NewGuid():N}";

        // Seed actual bytes so the cross-drive read step has something to copy. Source drive is
        // the fixture's encrypted drive; target drive defaults to encrypted as well (E→E, simplest
        // bytes-relocation shape). Bytes verification lives in TC011.
        var fileId = await fx.SeedFileWithBytesAsync(
            $"{folder}/source.txt",
            System.Text.Encoding.UTF8.GetBytes("tc003-payload"),
            encrypted: true);
        var secondDriveId = await fx.SeedSecondDriveAsync(encryptionEnabled: true);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var newPath = $"{folder}/moved.txt";
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{fileId}/move",
            new { targetPath = newPath, targetDriveId = secondDriveId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<MoveFileResponseDto>();
        body.Should().NotBeNull();
        body!.DriveId.Should().Be(secondDriveId);
        body.Path.Should().Be(newPath);

        var rowAfter = await fx.ReadFileAsync(fileId);
        rowAfter.Should().NotBeNull();
        rowAfter!.DriveId.Should().Be(secondDriveId);
        rowAfter.Path.Should().Be(newPath);
    }

    [Fact]
    public async Task TC004_PathTraversal_Returns400_AsValidationProblemDetails()
    {
        // STRG-085: traversal is now blocked at the request-body validator
        // (MoveFileRequestValidator) BEFORE the handler runs, so the wire envelope is RFC 7807
        // ValidationProblemDetails — not the legacy {code,message} shape that StoragePath.Parse
        // would have produced inside the handler. The handler-side StoragePath.Parse check is
        // retained as belt-and-suspenders for non-HTTP callers; this test pins the front-door
        // contract that HTTP traversal attempts surface as the validation envelope.
        var folder = $"tc004-{Guid.NewGuid():N}";
        var fileId = await fx.SeedFileAsync($"{folder}/source.txt");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{fileId}/move",
            new { targetPath = "../../etc/passwd", targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDocument>();
        problem.Should().NotBeNull();
        problem!.Title.Should().Be("Validation failed");
        problem.Status.Should().Be(400);
        problem.Errors.Should().ContainKey("targetPath");
        problem.Errors!["targetPath"].Should().ContainMatch("*'..'*");
    }

    [Fact]
    public async Task TC005_DirectoryMove_WithinDrive_Returns200_RootAndDescendantsRewritten()
    {
        // Phase 2 — flipped from rejection-pin to happy-path. Single descendant; the N-descendant
        // case (TC010) covers the streaming pattern more thoroughly.
        var folder = $"tc005-{Guid.NewGuid():N}";
        var dirId = await fx.SeedFileAsync($"{folder}/dir", isDirectory: true);
        var insideId = await fx.SeedFileAsync($"{folder}/dir/inside.txt");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{dirId}/move",
            new { targetPath = $"{folder}/renamed", targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var rootRow = await fx.ReadFileAsync(dirId);
        rootRow.Should().NotBeNull();
        rootRow!.Path.Should().Be($"{folder}/renamed");
        rootRow.Name.Should().Be("renamed");
        rootRow.IsDirectory.Should().BeTrue();

        var descendantRow = await fx.ReadFileAsync(insideId);
        descendantRow.Should().NotBeNull();
        descendantRow!.Path.Should().Be($"{folder}/renamed/inside.txt");
        descendantRow.Name.Should().Be("inside.txt"); // leaf invariant under directory rebase

        // No row should remain at the old prefix.
        await using var ctx = fx.NewDbContext();
        var oldPrefixCount = await ctx.Files
            .CountAsync(f => f.DriveId == fx.DriveId && f.Path.StartsWith($"{folder}/dir"));
        oldPrefixCount.Should().Be(0);
    }

    [Fact]
    public async Task TC006_MoveFile_EmitsFileMovedAuditEntry()
    {
        // Outbox round-trip is asserted via the audit row that AuditLogConsumer writes on
        // FileMovedEvent. Same 30s polling envelope as FileDeleteTests.TC004 — bare query
        // without retry would read pre-dispatch and flake.
        var folder = $"tc006-{Guid.NewGuid():N}";
        var fileId = await fx.SeedFileAsync($"{folder}/source.txt");
        var newPath = $"{folder}/moved/audit.txt";

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{fileId}/move",
            new { targetPath = newPath, targetDriveId = (Guid?)null });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        Strg.Core.Domain.AuditEntry? entry = null;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var ctx = fx.NewDbContext();
            entry = await ctx.AuditEntries
                .FirstOrDefaultAsync(e => e.Action == AuditActions.FileMoved && e.ResourceId == fileId);
            if (entry is not null)
            {
                break;
            }
            await Task.Delay(500);
        }

        entry.Should().NotBeNull("FileMovedEvent must reach AuditLogConsumer via the outbox");
        entry!.UserId.Should().Be(fx.UserId);
        entry.TenantId.Should().Be(fx.TenantId);
        entry.ResourceType.Should().Be(AuditResourceTypes.FileItem);
        entry.Details.Should().Contain($"\"driveId\":\"{fx.DriveId}\"");
        entry.Details.Should().Contain("\"oldPath\":");
        entry.Details.Should().Contain("\"newPath\":");
    }

    [Fact]
    public async Task TC007_MoveFile_FromWrongDrive_Returns404_FileUnchanged()
    {
        // Cross-drive id mismatch on the SOURCE collapses to 404 (not 403) so the wire shape
        // can't enumerate which drive a file belongs to. AC: "File in different drive → 404".
        // Capture original path BEFORE the move call, then assert it equals after — defensive
        // pin against a future regression where the wrong-drive path partially mutates.
        var folder = $"tc007-{Guid.NewGuid():N}";
        var fileId = await fx.SeedFileAsync($"{folder}/source.txt");
        var originalPath = (await fx.ReadFileAsync(fileId))!.Path;

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{Guid.NewGuid()}/files/{fileId}/move",
            new { targetPath = $"{folder}/new.txt", targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var rowAfter = await fx.ReadFileAsync(fileId);
        rowAfter.Should().NotBeNull();
        rowAfter!.Path.Should().Be(originalPath);
    }

    [Fact]
    public async Task TC008_Unauthenticated_Returns401()
    {
        var fileId = await fx.SeedFileAsync($"tc008-{Guid.NewGuid():N}.txt");

        using var client = fx.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{fileId}/move",
            new { targetPath = "anywhere.txt", targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TC009_WithoutFilesWriteScope_Returns403()
    {
        // RequireAuthorization(AuthPolicies.FilesWrite) gates the route — a token with files.read
        // but no files.write is rejected with 403 before the handler runs.
        var fileId = await fx.SeedFileAsync($"tc009-scope-{Guid.NewGuid():N}.txt");

        var token = await fx.AuthenticateWithScopesAsync("files.read files.share");
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{fileId}/move",
            new { targetPath = "anywhere.txt", targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TC010_DirectoryMove_WithinDrive_NDescendants_PrefixAnchorRespected()
    {
        // Phase 2 — N=5 descendants under the moved directory + a SIBLING file that shares the
        // same directory-name prefix without the trailing slash (e.g. "dir" vs "dirsibling.txt").
        // This pins the trailing-slash anchor inside MoveDirectoryWithinDriveAsync — without it,
        // the prefix match would silently move the sibling as well.
        var folder = $"tc010-{Guid.NewGuid():N}";
        var dirId = await fx.SeedFileAsync($"{folder}/dir", isDirectory: true);
        var aId = await fx.SeedFileAsync($"{folder}/dir/a.txt");
        var bId = await fx.SeedFileAsync($"{folder}/dir/b.txt");
        var cId = await fx.SeedFileAsync($"{folder}/dir/nested/c.txt");
        var dId = await fx.SeedFileAsync($"{folder}/dir/nested/d.txt");
        var eId = await fx.SeedFileAsync($"{folder}/dir/nested/deep/e.txt");
        var siblingId = await fx.SeedFileAsync($"{folder}/dirsibling.txt");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{dirId}/move",
            new { targetPath = $"{folder}/renamed", targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Root + 5 descendants under the new prefix.
        (await fx.ReadFileAsync(dirId))!.Path.Should().Be($"{folder}/renamed");
        (await fx.ReadFileAsync(aId))!.Path.Should().Be($"{folder}/renamed/a.txt");
        (await fx.ReadFileAsync(bId))!.Path.Should().Be($"{folder}/renamed/b.txt");
        (await fx.ReadFileAsync(cId))!.Path.Should().Be($"{folder}/renamed/nested/c.txt");
        (await fx.ReadFileAsync(dId))!.Path.Should().Be($"{folder}/renamed/nested/d.txt");
        (await fx.ReadFileAsync(eId))!.Path.Should().Be($"{folder}/renamed/nested/deep/e.txt");

        // Leaf names are invariant.
        (await fx.ReadFileAsync(eId))!.Name.Should().Be("e.txt");

        // The sibling (whose path STARTS with "dir" but isn't anchored at "dir/") MUST NOT have
        // moved. Without the trailing-slash anchor, this row would be silently rewritten too.
        (await fx.ReadFileAsync(siblingId))!.Path.Should().Be($"{folder}/dirsibling.txt");
    }

    [Fact]
    public async Task TC011_CrossDriveMove_SingleFile_BytesRoundTrip_E_to_E()
    {
        // Phase 2 — bytes-level verification of cross-drive E→E (encrypted source, encrypted
        // target with FRESH DEK). Asserts: (1) target download returns the original plaintext,
        // (2) source bytes are gone (best-effort delete completed), (3) source download collapses
        // to 404 because file.DriveId no longer matches the source drive route.
        var folder = $"tc011-{Guid.NewGuid():N}";
        var plaintext = System.Text.Encoding.UTF8.GetBytes("tc011-secret-payload-" + Guid.NewGuid());

        var fileId = await fx.SeedFileWithBytesAsync(
            $"{folder}/source.bin",
            plaintext,
            encrypted: true);

        // Capture the source storage key BEFORE move so we can assert absence after.
        string sourceStorageKey;
        Drive sourceDrive;
        await using (var ctx = fx.NewDbContext())
        {
            var version = await ctx.FileVersions.SingleAsync(v => v.FileId == fileId);
            sourceStorageKey = version.StorageKey;
            sourceDrive = await ctx.Drives.SingleAsync(d => d.Id == fx.DriveId);
        }

        var secondDriveId = await fx.SeedSecondDriveAsync(encryptionEnabled: true);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var newPath = $"{folder}/moved.bin";
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{fileId}/move",
            new { targetPath = newPath, targetDriveId = secondDriveId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // (1) Target download returns the original plaintext (encryption envelope decrypted on
        // the target drive's fresh DEK).
        var downloaded = await fx.ReadFileBytesAsync(secondDriveId, fileId, token);
        downloaded.Should().Equal(plaintext);

        // (2) Source bytes are gone on the source drive's provider.
        await fx.AssertStorageKeyAbsentAsync(sourceDrive, sourceStorageKey);

        // (3) Source-route download collapses to 404 because file.DriveId is now the target.
        using var sourceClient = fx.CreateAuthenticatedClient(token);
        using var srcResp = await sourceClient.GetAsync($"/api/v1/drives/{fx.DriveId}/files/{fileId}/content");
        srcResp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // (4) FileVersion was rebased (storage key flipped); FileKey was replaced with a fresh
        // DEK row (E→E). Both rows still exist, just with new envelope material.
        await using var ctx2 = fx.NewDbContext();
        var versionAfter = await ctx2.FileVersions.SingleAsync(v => v.FileId == fileId);
        versionAfter.StorageKey.Should().NotBe(sourceStorageKey);
        versionAfter.StorageKey.Should().Be(StrgUploadKeys.FinalKey(secondDriveId, fileId, versionAfter.VersionNumber));

        var fileKeyAfter = await ctx2.FileKeys.SingleAsync(k => k.FileVersionId == versionAfter.Id);
        fileKeyAfter.Algorithm.Should().Be(EncryptionAlgorithms.AesGcm256);
    }

    [Fact]
    public async Task TC012_CrossDriveMove_SingleFile_PlaintextToEncrypted_FileKeyAdded()
    {
        // Phase 2 — P→E cross-drive move. Source drive is plaintext; target is encrypted. The
        // handler must (a) read the plaintext bytes raw, (b) write through the encrypting writer,
        // (c) ADD a FileKey row for the target version. Round-trip via download proves the
        // encryption envelope is correctly assembled and decrypted on read.
        var folder = $"tc012-{Guid.NewGuid():N}";
        var plaintext = System.Text.Encoding.UTF8.GetBytes("tc012-plaintext-source-" + Guid.NewGuid());

        // The fixture's primary drive is encrypted; spin a plaintext drive specifically for this
        // test's source side.
        var plaintextSourceDriveId = await fx.SeedSecondDriveAsync(encryptionEnabled: false);
        var fileId = await fx.SeedFileWithBytesAsync(
            $"{folder}/plain.bin",
            plaintext,
            encrypted: false,
            driveId: plaintextSourceDriveId);
        var encryptedTargetDriveId = await fx.SeedSecondDriveAsync(encryptionEnabled: true);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var newPath = $"{folder}/moved.bin";
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{plaintextSourceDriveId}/files/{fileId}/move",
            new { targetPath = newPath, targetDriveId = encryptedTargetDriveId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Round-trip plaintext through the encrypted target.
        var downloaded = await fx.ReadFileBytesAsync(encryptedTargetDriveId, fileId, token);
        downloaded.Should().Equal(plaintext);

        // FileKey row was added for the target.
        await using var ctx = fx.NewDbContext();
        var version = await ctx.FileVersions.SingleAsync(v => v.FileId == fileId);
        var keyExists = await ctx.FileKeys.AnyAsync(k => k.FileVersionId == version.Id);
        keyExists.Should().BeTrue();
    }

    [Fact]
    public async Task TC013_CrossDriveMove_SingleFile_EncryptedToPlaintext_FileKeyRemoved()
    {
        // Phase 2 — E→P cross-drive move. Source drive is encrypted; target is plaintext. The
        // handler must (a) decrypt source via the source FileKey, (b) write plaintext bytes to
        // target raw, (c) REMOVE the source FileKey row (target plaintext drive has no FileKey).
        var folder = $"tc013-{Guid.NewGuid():N}";
        var plaintext = System.Text.Encoding.UTF8.GetBytes("tc013-payload-" + Guid.NewGuid());

        var fileId = await fx.SeedFileWithBytesAsync(
            $"{folder}/encrypted.bin",
            plaintext,
            encrypted: true);

        // FileKey id captured for post-move absence check.
        Guid versionId;
        await using (var ctx = fx.NewDbContext())
        {
            var version = await ctx.FileVersions.SingleAsync(v => v.FileId == fileId);
            versionId = version.Id;
            (await ctx.FileKeys.AnyAsync(k => k.FileVersionId == versionId)).Should().BeTrue();
        }

        var plaintextTargetDriveId = await fx.SeedSecondDriveAsync(encryptionEnabled: false);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var newPath = $"{folder}/moved.bin";
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{fileId}/move",
            new { targetPath = newPath, targetDriveId = plaintextTargetDriveId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Plaintext download from the plaintext target.
        var downloaded = await fx.ReadFileBytesAsync(plaintextTargetDriveId, fileId, token);
        downloaded.Should().Equal(plaintext);

        // Source FileKey row was removed.
        await using var ctx2 = fx.NewDbContext();
        var keyStillThere = await ctx2.FileKeys.AnyAsync(k => k.FileVersionId == versionId);
        keyStillThere.Should().BeFalse();
    }

    [Fact]
    public async Task TC014_CrossDriveDirectoryMove_Returns400_CrossDriveDirectoryUnsupported()
    {
        // Phase 2 — pins the v1.5 deferral. Directory + cross-drive together fall into the new
        // CrossDriveDirectoryUnsupported branch. Re-enabling the path is a deliberate change —
        // this regression test is the gate.
        var folder = $"tc014-{Guid.NewGuid():N}";
        var dirId = await fx.SeedFileAsync($"{folder}/dir", isDirectory: true);
        await fx.SeedFileAsync($"{folder}/dir/inside.txt");
        var secondDriveId = await fx.SeedSecondDriveAsync();

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{dirId}/move",
            new { targetPath = $"{folder}/renamed", targetDriveId = secondDriveId });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        body.Should().NotBeNull();
        body!.Code.Should().Be("CrossDriveDirectoryUnsupported");

        // Directory row UNCHANGED — neither path nor drive flipped on the rejection path.
        var dirRow = await fx.ReadFileAsync(dirId);
        dirRow.Should().NotBeNull();
        dirRow!.Path.Should().Be($"{folder}/dir");
        dirRow.DriveId.Should().Be(fx.DriveId);
    }

    [Fact]
    public async Task TC015_DirectoryMove_TargetPrefixCollision_Returns409()
    {
        // Phase 2 — pins the descendant-prefix collision check. The standard GetByPathAsync check
        // catches collision at the root path itself; the additional GetDescendantsAsync sweep
        // catches collisions one level deeper (a file already living under the target prefix).
        var folder = $"tc015-{Guid.NewGuid():N}";
        var dirId = await fx.SeedFileAsync($"{folder}/dir", isDirectory: true);
        await fx.SeedFileAsync($"{folder}/dir/inside.txt");

        // Pre-existing file under the target prefix — would be SHADOWED by the move otherwise.
        await fx.SeedFileAsync($"{folder}/renamed/already.txt");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{dirId}/move",
            new { targetPath = $"{folder}/renamed", targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Root row UNCHANGED on the conflict path.
        var rootAfter = await fx.ReadFileAsync(dirId);
        rootAfter.Should().NotBeNull();
        rootAfter!.Path.Should().Be($"{folder}/dir");
    }

    private sealed record MoveFileResponseDto(
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
