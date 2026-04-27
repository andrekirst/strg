using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace Strg.Integration.Tests.Listing;

/// <summary>
/// STRG-038 — REST file-listing endpoint integration tests. Class-scoped fixture
/// (<see cref="FileListFixture"/>) gives one PostgreSQL + RabbitMQ container shared across all
/// test methods. Each test scopes its seeded files under a unique top-level folder so state
/// from earlier tests in the same class can't bleed into later assertions.
/// </summary>
public sealed class FileListTests(FileListFixture fx) : IClassFixture<FileListFixture>
{
    [Fact]
    public async Task TC001_NonRecursive_ReturnsImmediateChildrenOnly()
    {
        // The "list root" framing in the issue's TC-001 is the non-recursive contract — only
        // direct children of the queried path appear. Queried under a unique sub-folder so
        // class-scope state from the other tests cannot bleed in.
        var folder = $"tc001-{Guid.NewGuid():N}";
        await fx.SeedFileAsync($"{folder}/alpha.txt");
        await fx.SeedFileAsync($"{folder}/beta.txt");
        await fx.SeedFileAsync($"{folder}/docs", isDirectory: true);
        await fx.SeedFileAsync($"{folder}/docs/nested.txt");          // must NOT appear non-recursively
        await fx.SeedFileAsync($"{folder}/docs/sub", isDirectory: true);
        await fx.SeedFileAsync($"{folder}/docs/sub/deep.txt");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.GetAsync($"/api/v1/drives/{fx.DriveId}/files?path=/{folder}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<FileListResponseDto>();
        body.Should().NotBeNull();
        body!.TotalCount.Should().Be(3);
        body.Items.Select(i => i.Name).Should().BeEquivalentTo(["alpha.txt", "beta.txt", "docs"]);
        body.Items.Should().NotContain(i => i.Path.Contains("nested.txt") || i.Path.Contains("deep.txt"));
    }

    [Fact]
    public async Task TC002_Recursive_ReturnsAllNestedItems()
    {
        var folder = $"tc002-{Guid.NewGuid():N}";
        await fx.SeedFileAsync($"{folder}/alpha.txt");
        await fx.SeedFileAsync($"{folder}/docs", isDirectory: true);
        await fx.SeedFileAsync($"{folder}/docs/nested.txt");
        await fx.SeedFileAsync($"{folder}/docs/sub", isDirectory: true);
        await fx.SeedFileAsync($"{folder}/docs/sub/deep.txt");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.GetAsync(
            $"/api/v1/drives/{fx.DriveId}/files?path=/{folder}&recursive=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<FileListResponseDto>();
        body.Should().NotBeNull();
        body!.TotalCount.Should().Be(5);
        body.Items.Select(i => i.Path).Should().BeEquivalentTo([
            $"{folder}/alpha.txt",
            $"{folder}/docs",
            $"{folder}/docs/nested.txt",
            $"{folder}/docs/sub",
            $"{folder}/docs/sub/deep.txt",
        ]);
    }

    [Fact]
    public async Task TC003_Pagination_ReturnsCorrectSlice()
    {
        var folder = $"tc003-{Guid.NewGuid():N}";
        for (var i = 0; i < 25; i++)
        {
            // Zero-pad so the alphabetic sort gives us a deterministic order — file-001.txt,
            // file-002.txt, ... file-025.txt — independent of locale collation.
            await fx.SeedFileAsync($"{folder}/file-{i:D3}.txt");
        }

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.GetAsync(
            $"/api/v1/drives/{fx.DriveId}/files?path=/{folder}&page=2&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<FileListResponseDto>();
        body.Should().NotBeNull();
        body!.Page.Should().Be(2);
        body.PageSize.Should().Be(10);
        body.TotalCount.Should().Be(25);
        body.Items.Should().HaveCount(10);
        body.Items[0].Name.Should().Be("file-010.txt"); // page 2 with size 10 starts at index 10
        body.Items[9].Name.Should().Be("file-019.txt");
    }

    [Fact]
    public async Task TC004_PageSize_CappedAt200()
    {
        // Seed 250 files so the cap-at-200 behaviour is observable: a page that nominally asks
        // for 999 must return at most 200 rows. Without the cap, the response would carry all
        // 250 (or whatever the request asked for).
        var folder = $"tc004-{Guid.NewGuid():N}";
        for (var i = 0; i < 250; i++)
        {
            await fx.SeedFileAsync($"{folder}/file-{i:D3}.txt");
        }

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.GetAsync(
            $"/api/v1/drives/{fx.DriveId}/files?path=/{folder}&pageSize=999");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<FileListResponseDto>();
        body.Should().NotBeNull();
        body!.PageSize.Should().Be(200);
        body.Items.Should().HaveCount(200);
        body.TotalCount.Should().Be(250);
    }

    [Fact]
    public async Task TC005_SoftDeletedFiles_ExcludedFromListing()
    {
        var folder = $"tc005-{Guid.NewGuid():N}";
        var keptId = await fx.SeedFileAsync($"{folder}/keep.txt");
        var deletedId = await fx.SeedFileAsync($"{folder}/gone.txt");
        await fx.SoftDeleteFileAsync(deletedId);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.GetAsync($"/api/v1/drives/{fx.DriveId}/files?path=/{folder}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<FileListResponseDto>();
        body.Should().NotBeNull();
        body!.TotalCount.Should().Be(1);
        body.Items.Should().ContainSingle(i => i.Id == keptId);
        body.Items.Should().NotContain(i => i.Id == deletedId);
    }

    [Fact]
    public async Task DirectoriesSortedBeforeFiles()
    {
        // Pins the "directories first, then alphabetical" acceptance criterion. Without an
        // explicit IsDirectory ordering, plain alphabetical would interleave dirs and files
        // (e.g. "apple.txt" before "bravo/" because 'a' < 'b').
        var folder = $"dirs-{Guid.NewGuid():N}";
        await fx.SeedFileAsync($"{folder}/apple.txt");
        await fx.SeedFileAsync($"{folder}/zulu", isDirectory: true);
        await fx.SeedFileAsync($"{folder}/bravo", isDirectory: true);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.GetAsync($"/api/v1/drives/{fx.DriveId}/files?path=/{folder}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<FileListResponseDto>();
        body.Should().NotBeNull();
        body!.Items.Should().HaveCount(3);
        body.Items[0].Name.Should().Be("bravo");           // dir, alphabetical first
        body.Items[0].IsDirectory.Should().BeTrue();
        body.Items[1].Name.Should().Be("zulu");            // dir
        body.Items[1].IsDirectory.Should().BeTrue();
        body.Items[2].Name.Should().Be("apple.txt");       // file last, despite alphabetical primacy
        body.Items[2].IsDirectory.Should().BeFalse();
    }

    [Fact]
    public async Task RootPath_ReturnsOnlyTopLevelItems()
    {
        // Pins the explicit AC: "GET /api/v1/drives/{driveId}/files?path=/ → returns root-level
        // items". Other TCs exercise non-recursive listing under a sub-folder; this one pins the
        // root case (path = "/" → empty prefix → top-level filter `!Path.Contains("/")`).
        // Uses GUID-suffixed names so concurrent tests in this class fixture cannot interfere.
        var marker = $"root-{Guid.NewGuid():N}";
        var rootFile = $"{marker}-top.txt";
        var nestedFolder = $"{marker}-folder";
        await fx.SeedFileAsync(rootFile);
        await fx.SeedFileAsync(nestedFolder, isDirectory: true);
        await fx.SeedFileAsync($"{nestedFolder}/inside.txt"); // must NOT appear at root listing

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.GetAsync($"/api/v1/drives/{fx.DriveId}/files?path=/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<FileListResponseDto>();
        body.Should().NotBeNull();
        // Filter to the items we seeded (other tests may have seeded their own root markers).
        var ours = body!.Items.Where(i => i.Name.StartsWith(marker)).ToArray();
        ours.Should().HaveCount(2);
        ours.Should().Contain(i => i.Name == rootFile && !i.IsDirectory);
        ours.Should().Contain(i => i.Name == nestedFolder && i.IsDirectory);
        ours.Should().NotContain(i => i.Path.Contains('/')); // no nested items under root
    }

    [Fact]
    public async Task UnknownDrive_Returns404()
    {
        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.GetAsync($"/api/v1/drives/{Guid.NewGuid()}/files");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WithoutFilesReadScope_Returns403()
    {
        // Authenticate with files.write but NOT files.read — the policy framework rejects with
        // 403 before the handler ever runs. Mirrors STRG-037's TC-004 pattern.
        var token = await fx.AuthenticateWithScopesAsync("files.write files.share");
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.GetAsync($"/api/v1/drives/{fx.DriveId}/files");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        using var client = fx.CreateClient();
        var response = await client.GetAsync($"/api/v1/drives/{fx.DriveId}/files");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed record FileListResponseDto(
        IReadOnlyList<FileItemRowDto> Items,
        int Page,
        int PageSize,
        int TotalCount);

    private sealed record FileItemRowDto(
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
