using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Auditing;
using Strg.Integration.Tests.Common;
using Xunit;

namespace Strg.Integration.Tests.Folders;

/// <summary>
/// STRG-042 — REST folder-creation endpoint integration tests. Class-scoped fixture
/// (<see cref="FolderCreateFixture"/>) gives one PostgreSQL + RabbitMQ container shared across
/// all test methods. Each test scopes its seeded paths under a unique top-level prefix so state
/// from earlier tests in the same class can't bleed into later assertions.
///
/// <para>Coverage maps to the issue body's four test cases:
/// <list type="bullet">
///   <item>TC-001 nested-path "a/b/c" with no existing parents → all segments created, ParentId
///   chained.</item>
///   <item>TC-002 idempotent re-POST of the same path → 200, no duplicate row.</item>
///   <item>TC-003 path traverses through an existing FILE → 409 Conflict.</item>
///   <item>TC-004 path traversal "../etc" → 400 InvalidPath.</item>
/// </list>
/// Plus extras: 404 on missing drive, 401 on missing token, single-segment happy path.</para>
/// </summary>
public sealed class FolderCreateTests(FolderCreateFixture fx) : IClassFixture<FolderCreateFixture>
{
    [Fact]
    public async Task TC001_NestedPath_AllSegmentsCreated_ParentIdChained()
    {
        var prefix = $"tc001-{Guid.NewGuid():N}";
        var path = $"{prefix}/a/b/c";

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/folders",
            new { path });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<FolderResponseDto>();
        body.Should().NotBeNull();
        body!.Path.Should().Be(path);
        body.Name.Should().Be("c");
        body.IsDirectory.Should().BeTrue();
        body.DriveId.Should().Be(fx.DriveId);

        // Every segment from the unique prefix down to the leaf must materialize as a directory
        // row, AND the ParentId chain must be wired in walk order. The prefix-as-segment test
        // shape doubles as the auto-create-parents proof: nothing was pre-seeded, so all four
        // rows came from the single POST.
        var prefixRow = await fx.ReadFileByPathAsync(prefix);
        var aRow = await fx.ReadFileByPathAsync($"{prefix}/a");
        var bRow = await fx.ReadFileByPathAsync($"{prefix}/a/b");
        var cRow = await fx.ReadFileByPathAsync($"{prefix}/a/b/c");

        prefixRow.Should().NotBeNull();
        aRow.Should().NotBeNull();
        bRow.Should().NotBeNull();
        cRow.Should().NotBeNull();

        prefixRow!.IsDirectory.Should().BeTrue();
        aRow!.IsDirectory.Should().BeTrue();
        bRow!.IsDirectory.Should().BeTrue();
        cRow!.IsDirectory.Should().BeTrue();

        prefixRow.ParentId.Should().BeNull("AC: top-level segment has no parent");
        aRow.ParentId.Should().Be(prefixRow.Id);
        bRow.ParentId.Should().Be(aRow.Id);
        cRow.ParentId.Should().Be(bRow.Id);

        body.Id.Should().Be(cRow.Id, "AC: returns the leaf FileItem");
    }

    [Fact]
    public async Task TC002_RepeatedPost_IsIdempotent_ReturnsSameRow_NoDuplicate()
    {
        var prefix = $"tc002-{Guid.NewGuid():N}";
        var path = $"{prefix}/docs";

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var first = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/folders",
            new { path });
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await first.Content.ReadFromJsonAsync<FolderResponseDto>();

        var second = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/folders",
            new { path });
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBody = await second.Content.ReadFromJsonAsync<FolderResponseDto>();

        secondBody!.Id.Should().Be(firstBody!.Id, "AC: idempotent re-entry returns the existing row");

        var rowCount = await fx.CountByPathAsync(path);
        rowCount.Should().Be(1, "AC: no duplicate folder created on second POST");
    }

    [Fact]
    public async Task TC003_PathSegmentCollidesWithExistingFile_Returns409()
    {
        var prefix = $"tc003-{Guid.NewGuid():N}";
        // Pre-seed a NON-directory row at "{prefix}/file.txt" so the next POST's walk hits a file
        // at the second segment. The handler must reject with Conflict before attempting to
        // create a child under a file row.
        await fx.SeedFileAsync($"{prefix}/file.txt");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/folders",
            new { path = $"{prefix}/file.txt/subdir" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // No row should have been created at the would-be subdir path. The prefix segment may or
        // may not have been auto-created depending on the walk's per-segment commit order — the
        // load-bearing assertion is that the FILE row is untouched and the SUBDIR was not created.
        var existingFile = await fx.ReadFileByPathAsync($"{prefix}/file.txt");
        existingFile.Should().NotBeNull();
        existingFile!.IsDirectory.Should().BeFalse("AC: collision must not mutate the existing file row");

        var subdir = await fx.ReadFileByPathAsync($"{prefix}/file.txt/subdir");
        subdir.Should().BeNull("AC: subdir under a file must not be created");
    }

    [Fact]
    public async Task TC004_PathTraversal_Returns400_AsValidationProblemDetails()
    {
        // STRG-085: traversal is now blocked at the request-body validator
        // (CreateFolderRequestValidator) BEFORE the handler runs, so the wire envelope is RFC 7807
        // ValidationProblemDetails — not the legacy {code,message} shape that StoragePath.Parse
        // would have produced inside the handler. The handler-side StoragePath.Parse check is
        // retained as belt-and-suspenders for non-HTTP callers; this test pins the front-door
        // contract that HTTP traversal attempts surface as the validation envelope.
        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/folders",
            new { path = "../etc" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDocument>();
        problem.Should().NotBeNull();
        problem!.Title.Should().Be("Validation failed");
        problem.Status.Should().Be(400);
        problem.Errors.Should().ContainKey("path");
        problem.Errors!["path"].Should().ContainMatch("*'..'*");
    }

    [Fact]
    public async Task TC001_EmptyPath_Returns400_AsValidationProblemDetails()
    {
        // STRG-085 TC-001 — POST /folders with empty path is now caught by the request-body
        // validator (NotEmpty rule on Path). The handler never runs.
        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/folders",
            new { path = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDocument>();
        problem.Should().NotBeNull();
        problem!.Errors.Should().ContainKey("path");
        problem.Errors!["path"].Should().ContainMatch("*required*");
    }

    [Fact]
    public async Task DriveDoesNotExist_Returns404()
    {
        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{Guid.NewGuid()}/folders",
            new { path = $"missing-drive-{Guid.NewGuid():N}" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        // Pins RequireAuthorization on the route — a request without a bearer token never reaches
        // the handler. The acceptance criterion "Requires files.write scope" decomposes into
        // (a) reject unauthenticated and (b) reject authenticated-without-scope, covered here and
        // in WithoutFilesWriteScope_Returns403 respectively.
        using var client = fx.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/folders",
            new { path = $"unauth-{Guid.NewGuid():N}" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WithoutFilesWriteScope_Returns403()
    {
        // RequireAuthorization(AuthPolicies.FilesWrite) gates the route — a token with files.read
        // but no files.write is rejected with 403 before the handler runs.
        var token = await fx.AuthenticateWithScopesAsync("files.read files.share");
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/folders",
            new { path = $"no-scope-{Guid.NewGuid():N}" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SingleSegment_Path_ParentIsNull()
    {
        var path = $"single-{Guid.NewGuid():N}";

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/folders",
            new { path });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await fx.ReadFileByPathAsync(path);
        row.Should().NotBeNull();
        row!.IsDirectory.Should().BeTrue();
        row.ParentId.Should().BeNull("top-level single segment has no parent");
    }

    [Fact]
    public async Task FreshCreate_EmitsSingleFolderCreatedAuditEntry_WithCreatedPathsList()
    {
        // Pins the handler's audit semantic: on a fresh nested create that materializes N rows,
        // exactly ONE folder.created audit row is written (resourceId = leaf), with the
        // createdPaths list in Details. Without this regression pin, a future refactor could
        // either drop the audit call entirely or split it into per-segment Records (the latter
        // would throw at runtime via IAuditScope.Record's single-call guard, but only after the
        // first segment's Record had succeeded — still a contract regression). Audit write goes
        // through AuditBehavior → IAuditService.LogAsync (synchronous in-process — see
        // src/Strg.Infrastructure/Auditing/AuditService.cs:54-58), so a direct read after the
        // HTTP response is sufficient; no polling envelope needed (unlike the file copy/move
        // audit tests, which assert the outbox→consumer round-trip).
        var prefix = $"audit-fresh-{Guid.NewGuid():N}";
        var path = $"{prefix}/a/b";

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/folders",
            new { path });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<FolderResponseDto>();
        body.Should().NotBeNull();

        await using var ctx = fx.NewDbContext();
        var entries = await ctx.AuditEntries
            .Where(e => e.Action == AuditActions.FolderCreated && e.ResourceId == body!.Id)
            .ToListAsync();

        entries.Should().ContainSingle("AC: one audit row per request, even when multiple parent segments are auto-created");
        var entry = entries[0];
        entry.UserId.Should().Be(fx.UserId);
        entry.TenantId.Should().Be(fx.TenantId);
        entry.ResourceType.Should().Be(AuditResourceTypes.FileItem);
        entry.Details.Should().NotBeNull();
        entry.Details!.Should().Contain($"driveId={fx.DriveId}");
        entry.Details.Should().Contain($"path={path}");
        entry.Details.Should().Contain("createdPaths=[", "Details must enumerate the actually-created segments");
        entry.Details.Should().Contain(prefix);
        entry.Details.Should().Contain($"{prefix}/a");
        entry.Details.Should().Contain(path);
    }

    [Fact]
    public async Task IdempotentRePost_EmitsNoAdditionalAuditEntry()
    {
        // Pins the inverse contract of FreshCreate_EmitsSingleFolderCreatedAuditEntry: when a
        // re-POST hits a path where every segment already exists as a directory, the handler's
        // `if (createdPaths.Count > 0)` guard MUST suppress the audit Record call entirely. A
        // future refactor that hoists Record out of the if-block would silently emit duplicate
        // audit rows for repeated POSTs (no exception, no wire-level change — pure audit-trail
        // pollution). This test catches that regression.
        var prefix = $"audit-idem-{Guid.NewGuid():N}";
        var path = $"{prefix}/docs";

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var first = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/folders",
            new { path });
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await first.Content.ReadFromJsonAsync<FolderResponseDto>();

        var second = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/folders",
            new { path });
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var ctx = fx.NewDbContext();
        var auditCount = await ctx.AuditEntries
            .CountAsync(e => e.Action == AuditActions.FolderCreated && e.ResourceId == firstBody!.Id);

        auditCount.Should().Be(1, "AC: idempotent re-entry creates no rows, so no second audit entry is written");
    }
}

/// <summary>
/// Mirrors the wire-shape of <c>FileItemDto</c> (defined in <c>FileListEndpoints</c>) for
/// deserialization on the test side. Per-test-class response DTOs are the project precedent
/// (<c>MoveFileResponseDto</c>, <c>CopyFileResponseDto</c>) — duplicating the small projection
/// avoids coupling the integration test assembly to the API's internal DTO type for what is
/// effectively a one-off wire-shape echo.
/// </summary>
internal sealed record FolderResponseDto(
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

/// <summary>
/// Wire-shape of the endpoint's error envelope for <c>InvalidPath</c> / <c>Conflict</c> /
/// <c>ValidationError</c> responses. The endpoint emits anonymous <c>{ code, message }</c> JSON
/// (see <c>FolderCreateEndpoints.CreateFolderAsync</c>); this record gives the test side a
/// strongly-typed shape so error-code assertions can branch on the code string instead of the
/// raw status code alone.
/// </summary>
internal sealed record FolderErrorDto(string Code, string Message);
