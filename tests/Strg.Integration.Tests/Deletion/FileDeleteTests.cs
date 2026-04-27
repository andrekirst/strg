using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Auditing;
using Xunit;

namespace Strg.Integration.Tests.Deletion;

/// <summary>
/// STRG-039 — REST file-delete endpoint integration tests. Class-scoped fixture
/// (<see cref="FileDeleteFixture"/>) gives one PostgreSQL + RabbitMQ container shared
/// across all test methods. Each test scopes its seeded files under a unique top-level
/// folder so state from earlier tests in the same class can't bleed into later assertions.
/// </summary>
public sealed class FileDeleteTests(FileDeleteFixture fx) : IClassFixture<FileDeleteFixture>
{
    [Fact]
    public async Task TC001_DeleteFile_Returns204_AndSubsequentDeleteReturns404()
    {
        var folder = $"tc001-{Guid.NewGuid():N}";
        var fileId = await fx.SeedFileAsync($"{folder}/target.txt");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var deleteResponse = await client.DeleteAsync($"/api/v1/drives/{fx.DriveId}/files/{fileId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Re-deleting a soft-deleted file: GetByIdAsync misses (global soft-delete filter)
        // → handler returns NotFound → endpoint maps to 404. Pins idempotency at the same
        // time as the AC's "subsequent GET → 404" intent.
        var redeleteResponse = await client.DeleteAsync($"/api/v1/drives/{fx.DriveId}/files/{fileId}");
        redeleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TC002_DeleteDirectory_MarksAllChildrenDeleted()
    {
        var folder = $"tc002-{Guid.NewGuid():N}";
        var rootDirId = await fx.SeedFileAsync(folder, isDirectory: true);
        var fileAId = await fx.SeedFileAsync($"{folder}/alpha.txt");
        var fileBId = await fx.SeedFileAsync($"{folder}/beta.txt");
        var subDirId = await fx.SeedFileAsync($"{folder}/sub", isDirectory: true);
        var nestedId = await fx.SeedFileAsync($"{folder}/sub/nested.txt");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.DeleteAsync($"/api/v1/drives/{fx.DriveId}/files/{rootDirId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // IgnoreQueryFilters required: the global soft-delete filter would otherwise hide
        // every row we just deleted and the assertion would read as "row missing" instead
        // of "row deleted".
        foreach (var id in new[] { rootDirId, fileAId, fileBId, subDirId, nestedId })
        {
            var row = await fx.ReadFileBypassingFiltersAsync(id);
            row.Should().NotBeNull($"row {id} must still exist (soft-delete, not hard-delete)");
            row!.DeletedAt.Should().NotBeNull($"row {id} must have DeletedAt set");
            row.IsDeleted.Should().BeTrue();
        }
    }

    [Fact]
    public async Task TC002b_DeleteDirectory_DoesNotTouchSiblingPrefixes()
    {
        // Anchoring guard: deleting "docs-X" must NOT also soft-delete "docs-Xbackup-…".
        // Without the trailing-'/' anchoring in the handler, StartsWith would match both
        // prefixes and the sibling tree would be wrongly destroyed. This is the regression
        // pin for the prefixSlash construction in DeleteFileHandler.
        var marker = Guid.NewGuid().ToString("N");
        var dirId = await fx.SeedFileAsync($"docs-{marker}", isDirectory: true);
        var dirChildId = await fx.SeedFileAsync($"docs-{marker}/inside.txt");
        var siblingId = await fx.SeedFileAsync($"docs-{marker}backup-untouchable.txt");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.DeleteAsync($"/api/v1/drives/{fx.DriveId}/files/{dirId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await fx.ReadFileBypassingFiltersAsync(dirId))!.DeletedAt.Should().NotBeNull();
        (await fx.ReadFileBypassingFiltersAsync(dirChildId))!.DeletedAt.Should().NotBeNull();

        // The sibling is the load-bearing assertion — it must NOT be deleted.
        (await fx.ReadFileBypassingFiltersAsync(siblingId))!.DeletedAt.Should().BeNull(
            "the trailing-'/' prefix anchor must prevent sibling-prefix matches");
    }

    [Fact]
    public async Task TC003_DeleteFile_FromWrongDrive_Returns404()
    {
        // Cross-drive id mismatch is collapsed to 404 (NOT 403) so the wire shape cannot
        // enumerate which drive a file belongs to. AC explicitly: "File in different drive
        // → 404 (not 403)".
        var fileId = await fx.SeedFileAsync($"tc003-{Guid.NewGuid():N}-victim.txt");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.DeleteAsync($"/api/v1/drives/{Guid.NewGuid()}/files/{fileId}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The file must NOT have been touched on the wrong-drive path.
        var row = await fx.ReadFileBypassingFiltersAsync(fileId);
        row!.DeletedAt.Should().BeNull(
            "wrong-drive route must not soft-delete the row in the file's actual drive");
    }

    [Fact]
    public async Task TC004_DeleteFile_EmitsFileDeletedAuditEntry()
    {
        // The outbox round-trip is asserted via the audit row that AuditLogConsumer writes
        // on FileDeletedEvent. Polling: the consumer runs after SaveChangesAsync commits and
        // the polling dispatcher drains the outbox — bare query without retry would read
        // pre-dispatch and flake. Same 30s envelope MassTransitOutboxTests applies.
        var fileId = await fx.SeedFileAsync($"tc004-{Guid.NewGuid():N}-audit.txt");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.DeleteAsync($"/api/v1/drives/{fx.DriveId}/files/{fileId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        Strg.Core.Domain.AuditEntry? entry = null;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var ctx = fx.NewDbContext();
            entry = await ctx.AuditEntries
                .FirstOrDefaultAsync(e => e.Action == AuditActions.FileDeleted && e.ResourceId == fileId);
            if (entry is not null)
            {
                break;
            }
            await Task.Delay(500);
        }

        entry.Should().NotBeNull("FileDeletedEvent must reach AuditLogConsumer via the outbox");
        entry!.UserId.Should().Be(fx.UserId);
        entry.TenantId.Should().Be(fx.TenantId);
        entry.ResourceType.Should().Be(AuditResourceTypes.FileItem);
        entry.Details.Should().Contain($"\"driveId\":\"{fx.DriveId}\"");
    }

    [Fact]
    public async Task TC005_Unauthenticated_Returns401()
    {
        var fileId = await fx.SeedFileAsync($"tc005-{Guid.NewGuid():N}.txt");

        using var client = fx.CreateClient();
        var response = await client.DeleteAsync($"/api/v1/drives/{fx.DriveId}/files/{fileId}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WithoutFilesWriteScope_Returns403()
    {
        // RequireAuthorization(AuthPolicies.FilesWrite) gates the route — a token with
        // files.read but no files.write is rejected with 403 before the handler runs.
        var fileId = await fx.SeedFileAsync($"scope-{Guid.NewGuid():N}.txt");

        var token = await fx.AuthenticateWithScopesAsync("files.read files.share");
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.DeleteAsync($"/api/v1/drives/{fx.DriveId}/files/{fileId}");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UnknownFile_Returns404()
    {
        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.DeleteAsync($"/api/v1/drives/{fx.DriveId}/files/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
