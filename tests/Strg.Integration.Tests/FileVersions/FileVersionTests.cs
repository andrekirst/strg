using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Strg.Integration.Tests.FileVersions;

/// <summary>
/// STRG-044 — file-versions endpoint integration tests. Covers TC-001..TC-003 from the issue
/// plus the Security Review checklist items (auth scope, storage-key not exposed) and the
/// HTTP Range acceptance criterion. One PostgreSQL + RabbitMQ container per test class via
/// <see cref="FileVersionFixture"/>.
/// </summary>
public sealed class FileVersionTests(FileVersionFixture fx) : IClassFixture<FileVersionFixture>
{
    private static readonly byte[] V1 = Encoding.UTF8.GetBytes("STRG-044 version 1 payload — first upload.");
    private static readonly byte[] V2 = Encoding.UTF8.GetBytes("STRG-044 version 2 payload — second upload, supersedes v1.");
    private static readonly byte[] V3 = Encoding.UTF8.GetBytes("STRG-044 version 3 payload — third upload, current head, longest line so range tests have room.");

    [Fact]
    public async Task TC001_ListVersions_AfterThreeUploads_ReturnsThreeEntriesDescending()
    {
        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var fileId = await fx.SeedEncryptedFileWithVersionsAsync(
            new[] { V1, V2, V3 },
            filename: "tc001.txt",
            mimeType: "text/plain");

        var response = await client.GetAsync($"/api/v1/drives/{fx.DriveId}/files/{fileId}/versions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var versions = (await response.Content.ReadFromJsonAsync<List<JsonElement>>())!;
        versions.Should().HaveCount(3);

        // Descending: latest first.
        versions[0].GetProperty("versionNumber").GetInt32().Should().Be(3);
        versions[1].GetProperty("versionNumber").GetInt32().Should().Be(2);
        versions[2].GetProperty("versionNumber").GetInt32().Should().Be(1);

        // Sizes match the seeded plaintexts (encryption envelope is NOT charged here per
        // STRG-026 #5 — Size is plaintext-denominated).
        versions[0].GetProperty("size").GetInt64().Should().Be(V3.LongLength);
        versions[1].GetProperty("size").GetInt64().Should().Be(V2.LongLength);
        versions[2].GetProperty("size").GetInt64().Should().Be(V1.LongLength);

        // Each row has a distinct content hash (different bytes → different SHA-256).
        versions[0].GetProperty("contentHash").GetString().Should().NotBe(versions[1].GetProperty("contentHash").GetString());
        versions[1].GetProperty("contentHash").GetString().Should().NotBe(versions[2].GetProperty("contentHash").GetString());
    }

    [Fact]
    public async Task TC002_DownloadVersion1_AfterMultipleVersions_ReturnsFirstUploadBytes()
    {
        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var fileId = await fx.SeedEncryptedFileWithVersionsAsync(
            new[] { V1, V2, V3 },
            filename: "tc002.txt",
            mimeType: "text/plain");

        var response = await client.GetAsync($"/api/v1/drives/{fx.DriveId}/files/{fileId}/versions/1/content");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Should().Equal(V1);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/plain");
        response.Content.Headers.ContentDisposition?.DispositionType.Should().Be("attachment");
        // Filename suffix encodes the version so the saved file isn't ambiguous with the head.
        response.Content.Headers.ContentDisposition?.FileName.Should().Contain("tc002.txt.v1");
        response.Headers.AcceptRanges.Should().Contain("bytes");
    }

    [Fact]
    public async Task TC003_DownloadNonexistentVersion_Returns404()
    {
        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var fileId = await fx.SeedEncryptedFileWithVersionsAsync(new[] { V1 }, filename: "tc003.bin");

        var response = await client.GetAsync($"/api/v1/drives/{fx.DriveId}/files/{fileId}/versions/999/content");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DownloadVersion_WithRange_Returns206_WithExactBytes()
    {
        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var fileId = await fx.SeedEncryptedFileWithVersionsAsync(
            new[] { V1, V2, V3 },
            filename: "range.txt",
            mimeType: "text/plain");

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/drives/{fx.DriveId}/files/{fileId}/versions/3/content");
        request.Headers.Range = new RangeHeaderValue(0, 9);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().Be(10);
        bytes.Should().Equal(V3.AsSpan(0, 10).ToArray());
        response.Content.Headers.ContentRange?.ToString().Should().Be($"bytes 0-9/{V3.LongLength}");
    }

    [Fact]
    public async Task ListVersions_OnNonexistentFile_Returns404()
    {
        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.GetAsync($"/api/v1/drives/{fx.DriveId}/files/{Guid.NewGuid()}/versions");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListVersions_WithoutFilesReadScope_Returns403()
    {
        // files.write alone — does NOT include files.read. The .RequireAuthorization policy on
        // the endpoint must reject this caller before any handler dispatch.
        var token = await fx.AuthenticateWithScopesAsync("files.write");
        using var client = fx.CreateAuthenticatedClient(token);
        var fileId = await fx.SeedEncryptedFileWithVersionsAsync(new[] { V1 });

        var response = await client.GetAsync($"/api/v1/drives/{fx.DriveId}/files/{fileId}/versions");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ListVersions_ResponseDoesNotLeakStorageKey()
    {
        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var fileId = await fx.SeedEncryptedFileWithVersionsAsync(new[] { V1, V2 }, filename: "leak-check.bin");

        var response = await client.GetAsync($"/api/v1/drives/{fx.DriveId}/files/{fileId}/versions");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Raw JSON sweep: no field name or value containing "storageKey" / "StorageKey" should
        // appear. The DTO is the contract surface; this guards against an accidental future
        // serializer change (e.g., a [JsonInclude] applied wholesale) leaking the locator.
        var rawJson = await response.Content.ReadAsStringAsync();
        rawJson.Should().NotContain("storageKey");
        rawJson.Should().NotContain("StorageKey");
    }
}
