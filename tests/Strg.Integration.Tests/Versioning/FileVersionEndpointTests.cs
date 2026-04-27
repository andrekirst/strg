using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Xunit;

namespace Strg.Integration.Tests.Versioning;

/// <summary>
/// STRG-044 — REST file-versions endpoint integration tests. Exercises both the list endpoint
/// (newest-first projection without <c>StorageKey</c>) and the per-version content stream
/// (HTTP Range support, 206 Partial Content). Class-scoped fixture
/// (<see cref="FileVersionEndpointFixture"/>) gives one PostgreSQL + RabbitMQ container shared
/// across every test method here; individual tests scope their seeded data under unique
/// filenames to keep state isolation cheap.
/// </summary>
public sealed class FileVersionEndpointTests(FileVersionEndpointFixture fx)
    : IClassFixture<FileVersionEndpointFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fx.SeedPlainDriveAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // TC-001: 3 versions seeded → list returns 3 entries DESC by versionNumber.
    [Fact]
    public async Task TC001_ListVersions_ReturnsAllVersions_OrderedDescending()
    {
        var v1 = Encoding.UTF8.GetBytes("STRG-044 v1 — first upload");
        var v2 = Encoding.UTF8.GetBytes("STRG-044 v2 — second upload, slightly longer to differ in size");
        var v3 = Encoding.UTF8.GetBytes("v3 — third upload, smallest of the three to discriminate sizes");
        var fileId = await fx.SeedFileWithVersionsAsync([v1, v2, v3], filename: "tc001.txt", mimeType: "text/plain");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.GetAsync(
            $"/api/v1/drives/{fx.PlainDriveId}/files/{fileId}/versions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<FileVersionRowDto[]>();
        body.Should().NotBeNull();
        body!.Should().HaveCount(3);

        // Descending order: 3 → 2 → 1.
        body!.Select(v => v.VersionNumber).Should().ContainInOrder(3, 2, 1);

        // Per-version sizes line up with the seeded payload lengths — proves the projection
        // pulls the right field (a bug that mirrored Size from the FileItem instead of from
        // FileVersion would still pass an "is descending" check but fail this size mapping).
        body![0].Size.Should().Be(v3.LongLength);
        body![1].Size.Should().Be(v2.LongLength);
        body![2].Size.Should().Be(v1.LongLength);

        // ContentHash matches per-version SHA-256.
        body![2].ContentHash.Should().Be(Convert.ToHexString(SHA256.HashData(v1)).ToLowerInvariant());
    }

    // Security checklist pin: StorageKey is NEVER in the wire JSON.
    [Fact]
    public async Task ListVersions_DoesNotExposeStorageKey()
    {
        var fileId = await fx.SeedFileWithVersionsAsync(
            [Encoding.UTF8.GetBytes("payload-for-storagekey-leak-test")],
            filename: "no-leak.txt");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.GetAsync(
            $"/api/v1/drives/{fx.PlainDriveId}/files/{fileId}/versions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync();

        // String-level grep on the raw JSON is the most direct anti-leak assertion: even a
        // future "well-meaning" addition of StorageKey to the projection would fail here, before
        // any deserialization-mapping rules could mask it.
        raw.Should().NotContain("storageKey", "StorageKey is provider-internal addressing — never exposed on the wire");
        raw.Should().NotContain("StorageKey");
        // versions/{guid:N} is the seeder's storage-key shape; confirm the actual value isn't
        // smuggled through under a different JSON property name.
        raw.Should().NotContain($"versions/{fileId:N}");
    }

    // TC-002: download v1 content → bytes equal first upload.
    [Fact]
    public async Task TC002_GetVersionContent_ReturnsOriginalBytes()
    {
        var v1 = Encoding.UTF8.GetBytes("first upload — TC-002 version-1 bytes");
        var v2 = Encoding.UTF8.GetBytes("second upload — these should NOT come back when asking for v1");
        var fileId = await fx.SeedFileWithVersionsAsync([v1, v2], filename: "tc002.bin");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.GetAsync(
            $"/api/v1/drives/{fx.PlainDriveId}/files/{fileId}/versions/1/content");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Should().Equal(v1, "version 1's stored bytes must round-trip exactly — proves StorageKey is per-version, not always-current");
        response.Content.Headers.ContentLength.Should().Be(v1.LongLength);
    }

    // TC-003: nonexistent version → 404.
    [Fact]
    public async Task TC003_GetVersionContent_NonexistentVersion_Returns404()
    {
        var fileId = await fx.SeedFileWithVersionsAsync(
            [Encoding.UTF8.GetBytes("only one version exists for this test")],
            filename: "tc003.bin");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.GetAsync(
            $"/api/v1/drives/{fx.PlainDriveId}/files/{fileId}/versions/999/content");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // AC: Range requests supported on version content download (206 Partial Content).
    [Fact]
    public async Task GetVersionContent_WithRangeHeader_Returns206PartialContent()
    {
        // Use a 200-byte payload so the 0-99 range is half the file — verifies both the slice
        // boundaries AND the 206 status code in the same call. enableRangeProcessing=true on
        // Results.File is the single line that drives this; a regression that drops it would
        // see the response come back as a plain 200 carrying the entire body.
        var payload = Encoding.UTF8.GetBytes(new string('x', 200));
        var fileId = await fx.SeedFileWithVersionsAsync([payload], filename: "range.bin");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/drives/{fx.PlainDriveId}/files/{fileId}/versions/1/content");
        request.Headers.Range = new RangeHeaderValue(0, 99);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().Be(100, "range 0-99 inclusive == 100 bytes");
        bytes.Should().Equal(payload.AsSpan(0, 100).ToArray());
        response.Content.Headers.ContentRange.Should().NotBeNull();
        response.Content.Headers.ContentRange!.From.Should().Be(0);
        response.Content.Headers.ContentRange.To.Should().Be(99);
        response.Content.Headers.ContentRange.Length.Should().Be(payload.LongLength);
    }

    // Tenant safety: GET versions list for an unknown file id returns 404 (the file lookup is
    // the tenant gate). A regression that dropped the IFileRepository.GetByIdAsync call before
    // GetVersionsAsync would surface here as a 200 with an empty array — the assertion polarity
    // ".Be(NotFound)" pins the right shape.
    [Fact]
    public async Task ListVersions_UnknownFile_Returns404()
    {
        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.GetAsync(
            $"/api/v1/drives/{fx.PlainDriveId}/files/{Guid.NewGuid()}/versions");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Capability-confusion guard: a file id from drive A addressed via drive B's route returns
    // 404, never the file's versions. Mirrors STRG-037's FileFromOtherDrive_Returns404.
    [Fact]
    public async Task ListVersions_FileFromOtherDrive_Returns404()
    {
        var fileId = await fx.SeedFileWithVersionsAsync(
            [Encoding.UTF8.GetBytes("payload")],
            filename: "wrong-drive.bin");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        // fx.DriveId is the inherited encrypted drive — the file lives on PlainDriveId.
        var response = await client.GetAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{fileId}/versions");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetVersionContent_FileFromOtherDrive_Returns404()
    {
        var fileId = await fx.SeedFileWithVersionsAsync(
            [Encoding.UTF8.GetBytes("payload")],
            filename: "wrong-drive-content.bin");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.GetAsync(
            $"/api/v1/drives/{fx.DriveId}/files/{fileId}/versions/1/content");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListVersions_WithoutFilesReadScope_Returns403()
    {
        var fileId = await fx.SeedFileWithVersionsAsync(
            [Encoding.UTF8.GetBytes("payload")],
            filename: "scope.bin");

        // Authenticate WITHOUT files.read; policy framework rejects with 403 before the handler runs.
        var token = await fx.AuthenticateWithScopesAsync("files.write files.share");
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.GetAsync(
            $"/api/v1/drives/{fx.PlainDriveId}/files/{fileId}/versions");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetVersionContent_WithoutFilesReadScope_Returns403()
    {
        var fileId = await fx.SeedFileWithVersionsAsync(
            [Encoding.UTF8.GetBytes("payload")],
            filename: "scope-content.bin");

        var token = await fx.AuthenticateWithScopesAsync("files.write files.share");
        using var client = fx.CreateAuthenticatedClient(token);
        var response = await client.GetAsync(
            $"/api/v1/drives/{fx.PlainDriveId}/files/{fileId}/versions/1/content");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ListVersions_Unauthenticated_Returns401()
    {
        using var client = fx.CreateClient();
        var response = await client.GetAsync(
            $"/api/v1/drives/{fx.PlainDriveId}/files/{Guid.NewGuid()}/versions");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Wire DTO mirror — kept private to the test to assert the exact field set the API emits
    /// without coupling the test to <c>FileVersionDto</c>'s internal type identity. If a future
    /// commit adds a field to <c>FileVersionDto</c>, tests that need to assert its presence will
    /// extend this shape; the silent-no-op risk is bounded.
    /// </summary>
    private sealed record FileVersionRowDto(
        int VersionNumber,
        long Size,
        string ContentHash,
        DateTimeOffset CreatedAt,
        Guid CreatedBy);
}
