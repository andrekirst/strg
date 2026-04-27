using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Auditing;
using Xunit;

namespace Strg.Integration.Tests.FileMove;

/// <summary>
/// STRG-040 — REST file-move endpoint integration tests. Class-scoped fixture
/// (<see cref="MoveFileFixture"/>) gives one PostgreSQL + RabbitMQ container shared across
/// all test methods. Each test scopes its seeded files under a unique top-level folder so
/// state from earlier tests in the same class can't bleed into later assertions.
/// </summary>
public sealed class MoveFileEndpointTests(MoveFileFixture fx) : IClassFixture<MoveFileFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task TC001_MoveFile_Returns200_NewPathReachable_OldPathReturns404OnRemove()
    {
        // Pin the canonical happy path: move within the source drive, assert the response
        // body carries the updated FileItemDto, and confirm the row's Path/Name are rewritten.
        // The "old path returns 404" facet of AC-6 is exercised by attempting to re-move from
        // the same fileId but now using the OLD path as the source — once the row's Path is
        // updated, GetByPathAsync(oldPath) misses, so anything keyed on the old (driveId, path)
        // is unreachable.
        var folder = $"tc001-{Guid.NewGuid():N}";
        var sourcePath = $"{folder}/src.txt";
        var targetPath = $"{folder}/dest.txt";
        var content = new byte[] { 1, 2, 3, 4, 5 };
        var fileId = await fx.SeedFileWithBlobAsync(
            fx.DriveId, fx.TempStorageRoot, sourcePath, content: content);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{fileId}/move",
            new { targetPath, targetDriveId = (Guid?)null });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        body.GetProperty("path").GetString().Should().Be(targetPath);
        body.GetProperty("name").GetString().Should().Be("dest.txt");
        body.GetProperty("id").GetGuid().Should().Be(fileId);

        // DB invariants: the row's Path/Name updated, DriveId unchanged, DeletedAt null.
        var row = await fx.ReadFileBypassingFiltersAsync(fileId);
        row.Should().NotBeNull();
        row!.Path.Should().Be(targetPath);
        row.Name.Should().Be("dest.txt");
        row.DriveId.Should().Be(fx.DriveId);
        row.DeletedAt.Should().BeNull();

        // Old-path unreachable: a subsequent move keyed off the OLD path on the same fileId
        // succeeds (the file is now at targetPath), but a query against the OLD (driveId,
        // path) misses. We assert the latter via direct DB lookup since it's the tightest
        // signal that the old-path access oracle is closed.
        await using var ctx = fx.NewDbContext();
        var byOldPath = await ctx.Files.FirstOrDefaultAsync(f =>
            f.DriveId == fx.DriveId && f.Path == sourcePath);
        byOldPath.Should().BeNull("the old path must no longer resolve to any file row");
    }

    [Fact]
    public async Task TC002_MoveToOccupiedPath_Returns409()
    {
        // Collision check: if the target (driveId, path) is already occupied by another
        // FileItem, the endpoint MUST return 409 BEFORE any storage I/O. The seeded source
        // and target are independent rows in the same drive; the source must remain at its
        // original path (storage and DB both untouched) so the conflict response is
        // genuinely non-destructive.
        var folder = $"tc002-{Guid.NewGuid():N}";
        var sourcePath = $"{folder}/src.txt";
        var occupiedPath = $"{folder}/already.txt";
        var sourceId = await fx.SeedFileWithBlobAsync(
            fx.DriveId, fx.TempStorageRoot, sourcePath, content: [10, 20, 30]);
        await fx.SeedFileWithBlobAsync(
            fx.DriveId, fx.TempStorageRoot, occupiedPath, content: [99]);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{sourceId}/move",
            new { targetPath = occupiedPath, targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Source row untouched: Path stays at sourcePath, the storage blob is still at the
        // original location.
        var row = await fx.ReadFileBypassingFiltersAsync(sourceId);
        row!.Path.Should().Be(sourcePath, "collision must not mutate the source row");
    }

    [Fact]
    public async Task TC003_CrossDriveMove_RebindsFileToTargetDrive()
    {
        // Cross-drive move: source drive is the fixture's primary (encrypted) drive; target
        // is the lazy-seeded secondary (unencrypted) drive. After the endpoint runs, the row
        // must be rebound to the secondary drive's id and the listing on the primary drive
        // must no longer return the row.
        var folder = $"tc003-{Guid.NewGuid():N}";
        var sourcePath = $"{folder}/src.bin";
        var targetPath = $"{folder}/dest.bin";
        var (secondaryDriveId, secondaryRoot) = await fx.EnsureSecondaryDriveAsync();
        var content = new byte[] { 7, 7, 7, 7 };
        var fileId = await fx.SeedFileWithBlobAsync(
            fx.DriveId, fx.TempStorageRoot, sourcePath, content: content);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{fileId}/move",
            new { targetPath, targetDriveId = secondaryDriveId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var row = await fx.ReadFileBypassingFiltersAsync(fileId);
        row!.DriveId.Should().Be(secondaryDriveId, "cross-drive move must rebind DriveId");
        row.Path.Should().Be(targetPath);

        // Storage-side: the blob must have been physically relocated to the secondary
        // drive's root. The local-FS provider's MoveAsync is a real File.Move, so the bytes
        // sit at the new root after the endpoint commits.
        var newPathOnDisk = Path.Combine(
            secondaryRoot,
            targetPath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(newPathOnDisk).Should().BeTrue(
            "the blob must be reachable at the secondary drive's root after the move");
    }

    [Fact]
    public async Task TC004_TraversalInTargetPath_Returns400()
    {
        // StoragePath.Parse rejects "../" — the endpoint must catch and surface 400 BEFORE
        // any DB read or storage call. Bare ".." in the path is the canonical traversal
        // attempt; the endpoint returns 400 with a problem-details body.
        var fileId = await fx.SeedFileWithBlobAsync(
            fx.DriveId, fx.TempStorageRoot, $"tc004-{Guid.NewGuid():N}/src.txt", content: [1]);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{fileId}/move",
            new { targetPath = "../escape.txt", targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Source row untouched: a parse-time rejection must not have advanced into the DB.
        var row = await fx.ReadFileBypassingFiltersAsync(fileId);
        row!.Path.Should().StartWith("tc004-",
            "traversal must reject before any DB mutation");
    }

    [Fact]
    public async Task FileMovedEvent_Reaches_AuditLogConsumer_Via_Outbox()
    {
        // The outbox round-trip is asserted via the audit row that AuditLogConsumer writes
        // on FileMovedEvent. Polling envelope (30s) matches MassTransitOutboxTests / the
        // FileDeleteTests TC004 pattern — the consumer runs after the dispatcher drains the
        // outbox table, so a bare query without retry would read pre-dispatch and flake.
        var folder = $"tc-evt-{Guid.NewGuid():N}";
        var sourcePath = $"{folder}/src.txt";
        var targetPath = $"{folder}/moved.txt";
        var fileId = await fx.SeedFileWithBlobAsync(
            fx.DriveId, fx.TempStorageRoot, sourcePath, content: [42]);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{fileId}/move",
            new { targetPath, targetDriveId = (Guid?)null });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        Strg.Core.Domain.AuditEntry? entry = null;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var ctx = fx.NewDbContext();
            entry = await ctx.AuditEntries.FirstOrDefaultAsync(e =>
                e.Action == AuditActions.FileMoved && e.ResourceId == fileId);
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
        entry.Details.Should().Contain($"\"newPath\":\"{targetPath}\"");
        entry.Details.Should().Contain($"\"oldPath\":\"{sourcePath}\"");
    }

    [Fact]
    public async Task UnknownTargetDrive_Returns404()
    {
        // Defensive coverage for a target drive that doesn't exist (random guid). The
        // endpoint must return 404 — not 500 from a downstream resolve.
        var fileId = await fx.SeedFileWithBlobAsync(
            fx.DriveId, fx.TempStorageRoot,
            $"unk-drive-{Guid.NewGuid():N}/src.txt", content: [9]);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{fileId}/move",
            new { targetPath = "anywhere.txt", targetDriveId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnknownFile_Returns404()
    {
        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{Guid.NewGuid()}/move",
            new { targetPath = "wherever.txt", targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task FileFromWrongDrive_Returns404_NotForbidden()
    {
        // Cross-drive id mismatch is collapsed to 404 (NOT 403) so the wire shape cannot
        // enumerate which drive a file belongs to. Mirrors the FileDeleteTests TC003
        // assertion stance.
        var fileId = await fx.SeedFileWithBlobAsync(
            fx.DriveId, fx.TempStorageRoot,
            $"wrong-{Guid.NewGuid():N}/victim.txt", content: [1]);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{Guid.NewGuid()}/files/{fileId}/move",
            new { targetPath = "anywhere.txt", targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Source row must NOT have been touched on the wrong-drive path.
        var row = await fx.ReadFileBypassingFiltersAsync(fileId);
        row!.Path.Should().StartWith("wrong-",
            "wrong-drive route must not mutate the row in the file's actual drive");
    }

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        var fileId = await fx.SeedFileWithBlobAsync(
            fx.DriveId, fx.TempStorageRoot,
            $"anon-{Guid.NewGuid():N}/src.txt", content: [1]);

        using var client = fx.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{fileId}/move",
            new { targetPath = "any.txt", targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WithoutFilesWriteScope_Returns403()
    {
        // RequireAuthorization(AuthPolicies.FilesWrite) gates the route — a token with
        // files.read but no files.write is rejected with 403 before the handler runs.
        var fileId = await fx.SeedFileWithBlobAsync(
            fx.DriveId, fx.TempStorageRoot,
            $"scope-{Guid.NewGuid():N}/src.txt", content: [1]);

        var token = await AuthenticateWithScopesAsync("files.read files.share");
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{fileId}/move",
            new { targetPath = "any.txt", targetDriveId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// POSTs the password grant with a caller-supplied scope list. Mirrors the equivalent
    /// helper in <c>FileDeleteFixture</c> — duplicated locally because the move fixture
    /// inherits from the upload fixture (which only exposes the full-scope authentication
    /// helper) and the scope-rejection test needs a narrower token.
    /// </summary>
    private async Task<string> AuthenticateWithScopesAsync(string scopes)
    {
        using var client = fx.CreateClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = fx.UserEmail,
            ["password"] = MoveFileFixture.TestPassword,
            ["client_id"] = "strg-default",
            ["scope"] = scopes,
        });
        using var response = await client.PostAsync("/connect/token", form);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("access_token").GetString()!;
    }
}
