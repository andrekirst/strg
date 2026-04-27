using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace Strg.Integration.Tests.Folders;

/// <summary>
/// STRG-042 — REST folder-creation endpoint integration tests. Class-scoped fixture
/// (<see cref="FolderEndpointFixture"/>) gives one PostgreSQL + RabbitMQ container shared across
/// every test method. Each test scopes its created folders under a unique top-level segment so
/// state from earlier tests in the same class can't bleed into later assertions (the
/// <c>(DriveId, Path)</c> unique index would otherwise produce flake-prone insert collisions
/// between tests).
/// </summary>
public sealed class FolderEndpointTests(FolderEndpointFixture fx) : IClassFixture<FolderEndpointFixture>
{
    [Fact]
    public async Task Tc001_CreatesAllParentSegments()
    {
        // TC-001 from STRG-042: POST path="a/b/c" with no existing parents → a, a/b, a/b/c all
        // created. The leaf row is what the endpoint returns; the parent rows must exist as
        // separate FileItem(IsDirectory=true) entries with the right ParentId chain.
        var marker = $"tc001-{Guid.NewGuid():N}";
        var leafPath = $"{marker}/b/c";

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/folders",
            new { path = leafPath });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<FolderResponseDto>();
        body.Should().NotBeNull();
        body!.Path.Should().Be(leafPath);
        body.IsDirectory.Should().BeTrue();
        body.Name.Should().Be("c");

        // Every intermediate directory must exist as a row, with the parent chain wired:
        //   marker          -> ParentId null
        //   marker/b        -> ParentId = id of marker
        //   marker/b/c      -> ParentId = id of marker/b
        var rootDir = await fx.GetByPathAsync(marker);
        var midDir = await fx.GetByPathAsync($"{marker}/b");
        var leafDir = await fx.GetByPathAsync(leafPath);

        rootDir.Should().NotBeNull();
        rootDir!.IsDirectory.Should().BeTrue();
        rootDir.ParentId.Should().BeNull("the top-level segment has no parent");

        midDir.Should().NotBeNull();
        midDir!.IsDirectory.Should().BeTrue();
        midDir.ParentId.Should().Be(rootDir.Id, "the middle segment's parent is the root segment row");

        leafDir.Should().NotBeNull();
        leafDir!.IsDirectory.Should().BeTrue();
        leafDir.ParentId.Should().Be(midDir.Id, "the leaf segment's parent is the middle segment row");
        leafDir.Id.Should().Be(body.Id, "the response body's id matches the persisted leaf row");
    }

    [Fact]
    public async Task Tc002_AlreadyExists_IsIdempotent_NoDuplicate()
    {
        // TC-002 from STRG-042: re-POSTing the same path returns 200 with the existing row and
        // does NOT create a second row. Exercises the idempotency carve-out that makes folder
        // creation safe to retry on transient client failures.
        var marker = $"tc002-{Guid.NewGuid():N}";

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var first = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/folders",
            new { path = marker });
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await first.Content.ReadFromJsonAsync<FolderResponseDto>();

        // Second call — same path, fresh request — must return 200 with the SAME id.
        var second = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/folders",
            new { path = marker });
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBody = await second.Content.ReadFromJsonAsync<FolderResponseDto>();

        secondBody!.Id.Should().Be(firstBody!.Id, "idempotent re-create must return the existing row, not a new one");

        // Verify the DB state directly: a single row with that path under our marker prefix.
        var count = await fx.CountByPathPrefixAsync(marker);
        count.Should().Be(1, "no duplicate folder row may be created on re-POST");
    }

    [Fact]
    public async Task Tc003_PathSegmentCollidesWithFile_Returns409()
    {
        // TC-003 from STRG-042: POST path="file.txt/subdir" where file.txt is a non-directory
        // FileItem → 409 Conflict. The endpoint must NOT silently auto-create a directory row
        // at the colliding path; it must surface the conflict to the caller.
        var marker = $"tc003-{Guid.NewGuid():N}";
        var fileSegment = $"{marker}/file.txt";

        // Pre-seed a non-directory file at the path the endpoint will try to walk through.
        await fx.SeedFileAsync(fileSegment, isDirectory: false);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/folders",
            new { path = $"{fileSegment}/subdir" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // The colliding row must remain a non-directory; the endpoint must NOT have flipped its
        // IsDirectory flag, and no "subdir" child must exist either.
        var collidingRow = await fx.GetByPathAsync(fileSegment);
        collidingRow.Should().NotBeNull();
        collidingRow!.IsDirectory.Should().BeFalse("the pre-existing file must remain a file");
        var subdir = await fx.GetByPathAsync($"{fileSegment}/subdir");
        subdir.Should().BeNull("no descendant may be created when the parent segment collision fires 409");
    }

    [Fact]
    public async Task Tc004_PathTraversal_Returns400()
    {
        // TC-004 from STRG-042: POST path="../etc" → 400. StoragePath.Parse rejects traversal
        // ("..") at parse time and the endpoint translates the StoragePathException to a
        // BadRequest result.
        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/folders",
            new { path = "../etc" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UnknownDrive_Returns404()
    {
        // Drive existence check is intentionally a 404 (not 403) to prevent enumeration of
        // drives belonging to other tenants — mirrors FileListEndpoints' UnknownDrive_Returns404.
        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{Guid.NewGuid()}/folders",
            new { path = "any" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WithoutFilesWriteScope_Returns403()
    {
        // Authenticate with files.read but NOT files.write — the policy framework rejects with
        // 403 before the handler ever runs. Mirrors FileListTests.WithoutFilesReadScope_Returns403.
        var token = await fx.AuthenticateWithScopesAsync("files.read files.share");
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/folders",
            new { path = $"scope-test-{Guid.NewGuid():N}" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        using var client = fx.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/folders",
            new { path = $"unauth-{Guid.NewGuid():N}" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ExistingDirectory_DescentContinues_NoDuplicate()
    {
        // Layered idempotency: when "a" exists as a directory, POST "a/b" must reuse "a" as the
        // parent — not duplicate it. Pins the "else if existing is directory → continue" branch
        // alongside the TC-002 full-match idempotency path.
        var marker = $"reuse-{Guid.NewGuid():N}";

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        // First request creates "marker" only.
        var step1 = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/folders",
            new { path = marker });
        step1.StatusCode.Should().Be(HttpStatusCode.OK);
        var step1Body = await step1.Content.ReadFromJsonAsync<FolderResponseDto>();

        // Second request asks for "marker/b" — must reuse step1Body.Id as parent.
        var step2 = await client.PostAsJsonAsync(
            $"/api/v1/drives/{fx.DriveId}/folders",
            new { path = $"{marker}/b" });
        step2.StatusCode.Should().Be(HttpStatusCode.OK);
        var step2Body = await step2.Content.ReadFromJsonAsync<FolderResponseDto>();

        var leaf = await fx.GetByPathAsync($"{marker}/b");
        leaf.Should().NotBeNull();
        leaf!.ParentId.Should().Be(step1Body!.Id, "the existing 'marker' folder must be reused as the parent of 'marker/b'");

        var count = await fx.CountByPathPrefixAsync(marker);
        count.Should().Be(2, "exactly two rows: 'marker' (reused) and 'marker/b' (new)");
    }

    private sealed record FolderResponseDto(
        Guid Id,
        string Name,
        string Path,
        long Size,
        string MimeType,
        bool IsDirectory,
        string? ContentHash,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
