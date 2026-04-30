using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace Strg.Integration.Tests.Listing;

/// <summary>
/// STRG-048 — REST integration tests for <c>?tagKey=</c>/<c>?tagValue=</c> on the file-listing
/// endpoint. Class-scoped fixture (<see cref="FileListFixture"/>) gives one PostgreSQL +
/// RabbitMQ container shared across all test methods. Each test scopes its files under a
/// unique GUID-suffixed folder so class-scope state from earlier tests cannot leak.
/// </summary>
public sealed class FileListTagFilterTests(FileListFixture fx) : IClassFixture<FileListFixture>
{
    // TC-003a — `?tagKey=project` (no value) returns every file tagged with that key,
    // regardless of value. Untagged files and files with other keys are excluded.
    [Fact]
    public async Task TC003a_TagKeyOnly_FiltersFilesWithThatKey()
    {
        var folder = $"tc003a-{Guid.NewGuid():N}";
        var taggedAcmeId = await fx.SeedFileAsync($"{folder}/acme.pdf");
        var taggedGlobexId = await fx.SeedFileAsync($"{folder}/globex.pdf");
        var untaggedId = await fx.SeedFileAsync($"{folder}/notes.txt");
        var otherKeyId = await fx.SeedFileAsync($"{folder}/other.txt");

        await fx.SeedTagAsync(taggedAcmeId, fx.UserId, "project", "acme");
        await fx.SeedTagAsync(taggedGlobexId, fx.UserId, "project", "globex");
        await fx.SeedTagAsync(otherKeyId, fx.UserId, "category", "draft");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.GetAsync(
            $"/api/v1/drives/{fx.DriveId}/files?path=/{folder}&tagKey=project");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<FileListResponseDto>();
        body.Should().NotBeNull();
        body!.TotalCount.Should().Be(2);
        body.Items.Select(i => i.Id).Should().BeEquivalentTo([taggedAcmeId, taggedGlobexId]);
        body.Items.Should().NotContain(i => i.Id == untaggedId || i.Id == otherKeyId);
    }

    // TC-003b — `?tagKey=project&tagValue=acme` requires an exact value match.
    [Fact]
    public async Task TC003b_TagKeyAndValue_FiltersFilesWithExactValue()
    {
        var folder = $"tc003b-{Guid.NewGuid():N}";
        var taggedAcmeId = await fx.SeedFileAsync($"{folder}/acme.pdf");
        var taggedGlobexId = await fx.SeedFileAsync($"{folder}/globex.pdf");
        await fx.SeedTagAsync(taggedAcmeId, fx.UserId, "project", "acme");
        await fx.SeedTagAsync(taggedGlobexId, fx.UserId, "project", "globex");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.GetAsync(
            $"/api/v1/drives/{fx.DriveId}/files?path=/{folder}&tagKey=project&tagValue=acme");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<FileListResponseDto>();
        body.Should().NotBeNull();
        body!.TotalCount.Should().Be(1);
        body.Items.Should().ContainSingle().Which.Id.Should().Be(taggedAcmeId);
    }

    // TC-001 (REST half) — User A's tag does NOT surface for User B's identical query.
    [Fact]
    public async Task TC001_TagFilterIsScopedToCurrentUser()
    {
        var folder = $"tc001-{Guid.NewGuid():N}";
        var sharedFileId = await fx.SeedFileAsync($"{folder}/shared.pdf");

        await fx.SeedTagAsync(sharedFileId, fx.UserId, "project", "acme");                     // user A
        var (userBId, tokenB) = await fx.CreateSecondUserAsync();
        await fx.SeedTagAsync(sharedFileId, userBId, "project", "globex");                     // user B

        // User A query: project=acme matches their tag.
        var tokenA = await fx.AuthenticateAsync();
        using (var clientA = fx.CreateAuthenticatedClient(tokenA))
        {
            var responseA = await clientA.GetAsync(
                $"/api/v1/drives/{fx.DriveId}/files?path=/{folder}&tagKey=project&tagValue=acme");
            responseA.StatusCode.Should().Be(HttpStatusCode.OK);
            var bodyA = await responseA.Content.ReadFromJsonAsync<FileListResponseDto>();
            bodyA!.TotalCount.Should().Be(1, "user A's project=acme tag matches");
        }

        // User B query: project=acme does NOT match — User A's tag is filtered out by both
        // the global TagUser query filter AND the inline t.UserId == userId predicate.
        using (var clientB = fx.CreateAuthenticatedClient(tokenB))
        {
            var responseB = await clientB.GetAsync(
                $"/api/v1/drives/{fx.DriveId}/files?path=/{folder}&tagKey=project&tagValue=acme");
            responseB.StatusCode.Should().Be(HttpStatusCode.OK);
            var bodyB = await responseB.Content.ReadFromJsonAsync<FileListResponseDto>();
            bodyB!.TotalCount.Should().Be(0, "user A's tag must not leak across to user B");
        }

        // Sanity: User B querying for THEIR OWN value still hits the file.
        using (var clientB2 = fx.CreateAuthenticatedClient(tokenB))
        {
            var responseB2 = await clientB2.GetAsync(
                $"/api/v1/drives/{fx.DriveId}/files?path=/{folder}&tagKey=project&tagValue=globex");
            responseB2.StatusCode.Should().Be(HttpStatusCode.OK);
            var bodyB2 = await responseB2.Content.ReadFromJsonAsync<FileListResponseDto>();
            bodyB2!.TotalCount.Should().Be(1, "user B sees their own project=globex tag");
        }
    }

    // Tag.Key normalization — input is lowercased server-side; storage already enforces lowercase
    // via Tag.Key's init-setter. Tag.Value is intentionally case-sensitive.
    [Fact]
    public async Task TagKey_IsCaseInsensitive_InputAndStorage()
    {
        var folder = $"caseins-{Guid.NewGuid():N}";
        var fileId = await fx.SeedFileAsync($"{folder}/doc.txt");
        // Caller passes mixed-case key; entity normalizes to "project" on init.
        await fx.SeedTagAsync(fileId, fx.UserId, "Project", "Acme");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        // Mixed-case input on the query side hits the lowercase row.
        var keyResponse = await client.GetAsync(
            $"/api/v1/drives/{fx.DriveId}/files?path=/{folder}&tagKey=PROJECT");
        var keyBody = await keyResponse.Content.ReadFromJsonAsync<FileListResponseDto>();
        keyBody!.TotalCount.Should().Be(1);

        // Case-sensitive value match — "Acme" hits, "acme" misses.
        var valueHitResponse = await client.GetAsync(
            $"/api/v1/drives/{fx.DriveId}/files?path=/{folder}&tagKey=project&tagValue=Acme");
        var valueHitBody = await valueHitResponse.Content.ReadFromJsonAsync<FileListResponseDto>();
        valueHitBody!.TotalCount.Should().Be(1);

        var valueMissResponse = await client.GetAsync(
            $"/api/v1/drives/{fx.DriveId}/files?path=/{folder}&tagKey=project&tagValue=acme");
        var valueMissBody = await valueMissResponse.Content.ReadFromJsonAsync<FileListResponseDto>();
        valueMissBody!.TotalCount.Should().Be(0, "Tag.Value matches case-sensitively");
    }

    private sealed record FileListResponseDto(
        IReadOnlyList<FileItemRowDto> Items,
        int Page,
        int PageSize,
        int TotalCount);

    private sealed record FileItemRowDto(
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
}
