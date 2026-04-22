using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;
using Strg.Integration.Tests.Auth;
using Xunit;

namespace Strg.Integration.Tests.WebDav;

/// <summary>
/// STRG-070 — WebDAV GET (with RFC 7233 Range) + PUT upload acceptance tests (TC-001..TC-005).
///
/// <para>Runs end-to-end through the real ASP.NET Core pipeline: OpenIddict token mint → Bearer
/// auth → <c>/dav</c> Map branch → <see cref="Strg.WebDav.StrgWebDavMiddleware"/> →
/// <see cref="Strg.WebDav.StrgWebDavStore"/> → <c>LocalFileSystemProvider</c>. No mocks; the
/// Commit-first quota path, the HashingStream SHA-256 pass, and the FileItem+FileVersion DB writes
/// all execute for real. A per-test-class temp directory backs the drive's <c>rootPath</c>, so
/// blob bytes land on the real filesystem and round-trip through <c>IStorageProvider.ReadAsync</c>
/// on the GET side.</para>
///
/// <para><b>Why this class, not the existing <see cref="WebDavStoreTests"/>.</b> STRG-068 pins
/// GET as a plain 200 full-body delivery; STRG-070 adds PUT, Range requests, ETag headers,
/// Accept-Ranges, and the quota short-circuit. Keeping them separate stops either suite from
/// bleeding into the other's failure surface when a regression hits — a Range-parsing bug here
/// should not be diagnosed as a PROPFIND breakage.</para>
///
/// <para><b>Admin QuotaBytes manipulation.</b> TC-003 reduces the admin user's <c>QuotaBytes</c>
/// to a small number so the genuine <c>QuotaService.CommitAsync</c> atomic UPDATE fails and raises
/// <see cref="Strg.Core.Exceptions.QuotaExceededException"/>. Mocking <c>IQuotaService</c> instead
/// would undermine the whole point of the pin: the race-safe Commit-first behavior is what we're
/// verifying, and swapping the implementation would erase it.</para>
/// </summary>
public sealed class WebDavPutGetTests(StrgWebApplicationFactory factory)
    : IClassFixture<StrgWebApplicationFactory>, IAsyncLifetime
{
    private const string DriveName = "put-get-test-drive";

    private string _rootPath = string.Empty;
    private Guid _driveId;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), $"strg-webdav-put-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootPath);
        _driveId = await EnsureDriveAsync();
        // Reset the admin's used-bytes counter and give them the default 10 GB so a previous test
        // in the class doesn't leave the quota in an unexpected state. The class fixture is shared,
        // so TC-003's QuotaBytes shrink would otherwise poison TC-001/TC-004 if they ran after it.
        await ResetAdminQuotaAsync(quotaBytes: 10L * 1024 * 1024 * 1024, usedBytes: 0);
    }

    Task IAsyncLifetime.DisposeAsync()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TC001_put_new_file_creates_201_and_get_round_trips_bytes()
    {
        var expected = Encoding.UTF8.GetBytes("round-trip payload — the quick brown fox");
        var client = await CreateAuthenticatedClientAsync();

        using var putResponse = await PutAsync(client, "alpha.txt", expected);
        putResponse.StatusCode.Should().Be(HttpStatusCode.Created,
            because: "RFC 4918 §9.7 — PUT on a previously non-existent resource returns 201 Created");
        putResponse.Headers.ETag.Should().NotBeNull(
            because: "STRG-070 emits an ETag header on successful PUT so clients can cache-key the upload");

        using var getResponse = await client.GetAsync($"/dav/{DriveName}/alpha.txt");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var actual = await getResponse.Content.ReadAsByteArrayAsync();
        actual.Should().Equal(expected,
            because: "the GET body must be byte-identical to the bytes the client PUT — streaming round-trip invariant");

        // FileItem + FileVersion row pin: v1 FileVersion is the audit trail STRG-043 Prune keys off.
        await AssertDbStateAsync("alpha.txt", expectedSize: expected.LongLength, expectedVersion: 1);
    }

    [Fact]
    public async Task TC002_get_with_range_returns_206_partial_content_and_exact_window()
    {
        // 256 deterministic bytes (0..255) so the byte-window assertion is exact.
        var payload = new byte[256];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }
        var client = await CreateAuthenticatedClientAsync();

        using var putResponse = await PutAsync(client, "range.bin", payload);
        putResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/dav/{DriveName}/range.bin");
        request.Headers.Range = new RangeHeaderValue(100, 199);
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent,
            because: "RFC 7233 §4.1 — a satisfiable single-range GET MUST return 206 Partial Content");
        response.Content.Headers.ContentLength.Should().Be(100,
            because: "Content-Length on a 206 equals the length of the returned window, not the full resource");
        response.Content.Headers.ContentRange!.Unit.Should().Be("bytes");
        response.Content.Headers.ContentRange.From.Should().Be(100);
        response.Content.Headers.ContentRange.To.Should().Be(199);
        response.Content.Headers.ContentRange.Length.Should().Be(payload.LongLength);

        var actual = await response.Content.ReadAsByteArrayAsync();
        actual.Should().HaveCount(100);
        actual.Should().Equal(payload.AsSpan(100, 100).ToArray(),
            because: "the returned bytes must be the exact window the client asked for (offsets 100..199)");
    }

    [Fact]
    public async Task TC003_put_over_quota_returns_507_and_does_not_persist_file()
    {
        // Shrink admin quota to 8 bytes so a 64-byte PUT fails Commit. Forcing the genuine
        // QuotaService.CommitAsync failure (not a mock) is what makes this a real Commit-first pin.
        await ResetAdminQuotaAsync(quotaBytes: 8, usedBytes: 0);

        var payload = new byte[64];
        Array.Fill(payload, (byte)0xAB);
        var client = await CreateAuthenticatedClientAsync();

        using var response = await PutAsync(client, "too-big.bin", payload);
        response.StatusCode.Should().Be((HttpStatusCode)507,
            because: "RFC 4918 §9.7.3 — insufficient storage maps directly to 507; mirroring the QuotaService atomic-UPDATE miss");

        // Compensating-action pin: the blob was written then reaped, and no FileItem landed.
        await using var sp = BuildScopedDb();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var persisted = await db.Files
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.DriveId == _driveId && f.Path == "too-big.bin");
        persisted.Should().BeNull(
            because: "a 507-rejected PUT must leave no FileItem row — otherwise the quota overshoot is observable as a ghost file");

        // Restore the admin's quota so downstream tests in the class aren't starved.
        await ResetAdminQuotaAsync(quotaBytes: 10L * 1024 * 1024 * 1024, usedBytes: 0);
    }

    [Fact]
    public async Task TC004_put_overwrite_returns_204_and_appends_file_version()
    {
        var first = Encoding.UTF8.GetBytes("v1 content");
        var second = Encoding.UTF8.GetBytes("v2 content — a different payload");
        var client = await CreateAuthenticatedClientAsync();

        using (var putV1 = await PutAsync(client, "versioned.txt", first))
        {
            putV1.StatusCode.Should().Be(HttpStatusCode.Created);
        }
        using (var putV2 = await PutAsync(client, "versioned.txt", second))
        {
            putV2.StatusCode.Should().Be(HttpStatusCode.NoContent,
                because: "RFC 4918 §9.7 — PUT on an existing resource returns 204 No Content");
        }

        // GET after overwrite returns v2 bytes: StorageKey flipped to the fresh blob.
        using var getResponse = await client.GetAsync($"/dav/{DriveName}/versioned.txt");
        var actual = await getResponse.Content.ReadAsByteArrayAsync();
        actual.Should().Equal(second);

        // Two FileVersion rows — v1 and v2 — prove STRG-043 Prune has history to act on.
        await using var sp = BuildScopedDb();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var file = await db.Files
            .AsNoTracking()
            .SingleAsync(f => f.DriveId == _driveId && f.Path == "versioned.txt");
        file.VersionCount.Should().Be(2, because: "FileItem.VersionCount increments on every PUT");

        var versions = await db.FileVersions
            .AsNoTracking()
            .Where(v => v.FileId == file.Id)
            .OrderBy(v => v.VersionNumber)
            .ToListAsync();
        versions.Should().HaveCount(2);
        versions[0].VersionNumber.Should().Be(1);
        versions[0].Size.Should().Be(first.LongLength);
        versions[1].VersionNumber.Should().Be(2);
        versions[1].Size.Should().Be(second.LongLength);
        versions[0].StorageKey.Should().NotBe(versions[1].StorageKey,
            because: "opaque per-write keys preserve v1's blob — overwriting the same key would destroy history");
    }

    [Fact]
    public async Task TC005_head_returns_headers_without_body()
    {
        var payload = Encoding.UTF8.GetBytes("head request probe");
        var client = await CreateAuthenticatedClientAsync();

        using var putResponse = await PutAsync(client, "probe.txt", payload);
        putResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var request = new HttpRequestMessage(HttpMethod.Head, $"/dav/{DriveName}/probe.txt");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentLength.Should().Be(payload.LongLength,
            because: "RFC 7231 §4.3.2 — HEAD MUST return the same Content-Length a GET would, so clients can size before downloading");
        response.Headers.AcceptRanges.Should().Contain("bytes",
            because: "STRG-070 advertises byte-range support unconditionally on GET/HEAD");
        response.Headers.ETag.Should().NotBeNull(
            because: "HEAD must surface the same ETag as GET so conditional revalidation works without a body transfer");

        var bodyBytes = await response.Content.ReadAsByteArrayAsync();
        bodyBytes.Should().BeEmpty(because: "HEAD never carries a body");
    }

    [Fact]
    public async Task TC006_put_without_files_write_scope_returns_403()
    {
        // Mint a token WITHOUT files.write. Every other scope the admin user carries stays intact;
        // this pins that the middleware's explicit HasScope gate is what enforces files.write
        // (endpoint-routing metadata doesn't reach manually-wired middleware).
        using var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword,
            scopes: "files.read");
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var (accessToken, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);
        var client = factory.CreateAuthenticatedClient(accessToken);

        using var response = await PutAsync(client, "forbidden.txt", "nope"u8.ToArray());
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "authenticated-but-unscoped clients must hit 403, not 401 — the credential is valid, the permission is missing");
    }

    // ---- helpers ----

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string path, byte[] body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/dav/{DriveName}/{path}")
        {
            // Content-Type deliberately omitted — the middleware's octet-stream fallback is what
            // gets exercised. RFC 4918 §9.7 makes Content-Type optional on PUT.
            Content = new ByteArrayContent(body),
        };
        return await client.SendAsync(request);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        using var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var (accessToken, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);
        return factory.CreateAuthenticatedClient(accessToken);
    }

    private async Task<Guid> EnsureDriveAsync()
    {
        await using var sp = BuildScopedDb();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();

        var existing = await db.Drives.FirstOrDefaultAsync(d => d.Name == DriveName);
        if (existing is not null)
        {
            existing.ProviderConfig = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["rootPath"] = _rootPath,
            });
            await db.SaveChangesAsync();
            return existing.Id;
        }

        var drive = new Drive
        {
            TenantId = factory.AdminTenantId,
            Name = DriveName,
            ProviderType = "local",
            ProviderConfig = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["rootPath"] = _rootPath,
            }),
        };
        db.Drives.Add(drive);
        await db.SaveChangesAsync();
        return drive.Id;
    }

    private async Task ResetAdminQuotaAsync(long quotaBytes, long usedBytes)
    {
        await using var sp = BuildScopedDb();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var admin = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == factory.AdminUserId);
        admin.QuotaBytes = quotaBytes;
        admin.UsedBytes = usedBytes;
        await db.SaveChangesAsync();
    }

    private async Task AssertDbStateAsync(string path, long expectedSize, int expectedVersion)
    {
        await using var sp = BuildScopedDb();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var file = await db.Files
            .AsNoTracking()
            .SingleAsync(f => f.DriveId == _driveId && f.Path == path);
        file.Size.Should().Be(expectedSize);
        file.VersionCount.Should().Be(expectedVersion);
        file.ContentHash.Should().NotBeNullOrEmpty(
            because: "HashingStream computes SHA-256 in-flight and persists it on FileItem — null would mean the hash pump never ran");

        var versionCount = await db.FileVersions
            .AsNoTracking()
            .CountAsync(v => v.FileId == file.Id);
        versionCount.Should().Be(expectedVersion,
            because: "every PUT must append exactly one FileVersion row — that's the contract STRG-043 Prune relies on");
    }

    private ServiceProvider BuildScopedDb()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new FixtureTenantContext(factory.AdminTenantId));
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(factory.ConnectionString).UseOpenIddict());
        return services.BuildServiceProvider();
    }

    private sealed class FixtureTenantContext(Guid tenantId) : ITenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }
}
