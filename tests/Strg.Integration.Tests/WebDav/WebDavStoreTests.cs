using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;
using Strg.Integration.Tests.Auth;
using Xunit;

namespace Strg.Integration.Tests.WebDav;

/// <summary>
/// STRG-068 — StrgWebDavStore + PROPFIND / GET wiring acceptance tests (TC-001..TC-005).
///
/// <para>Runs against the real <see cref="StrgWebApplicationFactory"/> so PROPFIND goes through
/// the full ASP.NET Core pipeline, the authentication middleware, the <c>/dav</c>-branched
/// <see cref="Strg.WebDav.StrgWebDavMiddleware"/>, and finally the EF-backed
/// <c>StrgWebDavStore</c>. A per-test temp directory is registered as the drive's
/// <c>rootPath</c> so TC-003 can stream real bytes through the
/// <c>LocalFileSystemProvider</c>.</para>
/// </summary>
public sealed class WebDavStoreTests(StrgWebApplicationFactory factory)
    : IClassFixture<StrgWebApplicationFactory>, IAsyncLifetime
{
    private const string DriveName = "store-test-drive";
    private const string XmlNs = "DAV:";

    private string _rootPath = string.Empty;
    private Guid _driveId;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), $"strg-webdav-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootPath);
        _driveId = await EnsureDriveAsync();
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
    public async Task TC001_propfind_root_returns_multistatus_xml_with_root_items()
    {
        await SeedFileAsync(name: "readme.txt", path: "readme.txt", content: "hello"u8.ToArray());
        await SeedFolderAsync(name: "docs", path: "docs");

        var accessToken = await AcquireAccessTokenAsync();
        var client = factory.CreateAuthenticatedClient(accessToken);

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"/dav/{DriveName}/");
        request.Headers.Add("Depth", "1");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.MultiStatus);
        var xml = await response.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(xml);
        var responses = doc.Descendants(XName.Get("response", XmlNs)).ToList();
        responses.Should().HaveCountGreaterThanOrEqualTo(3,
            because: "drive root itself + 'readme.txt' + 'docs' should all be rendered as <D:response> elements");

        var hrefs = responses
            .Select(r => r.Element(XName.Get("href", XmlNs))?.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList();
        hrefs.Should().Contain(h => h!.EndsWith("/readme.txt", StringComparison.Ordinal));
        hrefs.Should().Contain(h => h!.EndsWith("/docs", StringComparison.Ordinal)
            || h!.EndsWith("/docs/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TC002_propfind_excludes_soft_deleted_files()
    {
        await SeedFileAsync(name: "keep.txt", path: "keep.txt", content: "keep"u8.ToArray());
        await SeedFileAsync(name: "deleted.txt", path: "deleted.txt", content: "gone"u8.ToArray(), isDeleted: true);

        var accessToken = await AcquireAccessTokenAsync();
        var client = factory.CreateAuthenticatedClient(accessToken);

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"/dav/{DriveName}/");
        request.Headers.Add("Depth", "1");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.MultiStatus);
        var xml = await response.Content.ReadAsStringAsync();
        xml.Should().Contain("keep.txt");
        xml.Should().NotContain("deleted.txt",
            because: "soft-deleted FileItems must not leak via WebDAV listings — the global query filter excludes them");
    }

    [Fact]
    public async Task TC003_get_on_file_returns_200_with_bytes()
    {
        var expected = Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog");
        await SeedFileAsync(name: "fox.txt", path: "fox.txt", content: expected);

        var accessToken = await AcquireAccessTokenAsync();
        var client = factory.CreateAuthenticatedClient(accessToken);

        using var response = await client.GetAsync($"/dav/{DriveName}/fox.txt");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var actual = await response.Content.ReadAsByteArrayAsync();
        actual.Should().Equal(expected);
    }

    [Fact]
    public async Task TC004_propfind_on_unsafe_path_returns_400()
    {
        var accessToken = await AcquireAccessTokenAsync();
        var client = factory.CreateAuthenticatedClient(accessToken);

        // Originally this test used "%2e%2e/etc/passwd" to pin traversal rejection, but the
        // TestServer's HttpClient canonicalizes percent-encoded dot segments per RFC 3986 §5.2.4
        // before the middleware ever sees the URL — the path collapses to /dav/etc/passwd and we
        // get 404 from drive resolution, not 400 from WebDavUriParser. A reserved Windows device
        // name in the last segment is a URL-boundary attack that survives Uri canonicalization
        // intact: StoragePath.IsReservedName catches it and throws StoragePathException, which
        // the middleware maps to 400. Same invariant (URL boundary fail-closed), reliable wire
        // shape.
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"),
            $"/dav/{DriveName}/CON");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "unsafe paths must be rejected at the URL boundary by StoragePath.Parse, not passed through to the storage provider");
    }

    [Fact]
    public async Task TC005_propfind_on_different_tenant_drive_returns_404()
    {
        // A drive belonging to a different tenant is indistinguishable from a non-existent drive
        // — returning 404 instead of 403 is a deliberate choice pinned in STRG-067 TC-003:
        // distinguishing the two cases would be an enumeration oracle leaking drive existence
        // across tenant boundaries. The spec's TC-005 mentions 403; the 404 here is the
        // repo-wide convention the DriveResolver already enforces.
        var otherTenantId = await SeedDriveInOtherTenantAsync("other-tenant-drive");
        otherTenantId.Should().NotBe(factory.AdminTenantId);

        var accessToken = await AcquireAccessTokenAsync();
        var client = factory.CreateAuthenticatedClient(accessToken);

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/other-tenant-drive/");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            because: "cross-tenant drive resolution must fail closed as 404 to avoid leaking drive existence");
    }

    // ---- helpers ----

    private async Task<string> AcquireAccessTokenAsync()
    {
        using var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var (accessToken, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);
        return accessToken;
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

    private async Task<Guid> SeedDriveInOtherTenantAsync(string driveName)
    {
        await using var sp = BuildScopedDb();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();

        var existing = await db.Drives.IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Name == driveName);
        if (existing is not null)
        {
            return existing.TenantId;
        }

        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = $"other-tenant-{tenantId:N}" });

        db.Drives.Add(new Drive
        {
            TenantId = tenantId,
            Name = driveName,
            ProviderType = "local",
            ProviderConfig = "{}",
        });
        await db.SaveChangesAsync();
        return tenantId;
    }

    private async Task SeedFileAsync(string name, string path, byte[] content, bool isDeleted = false)
    {
        // Write bytes under the drive's rootPath at the same relative path — the FileItem's
        // StorageKey must match the relative path the LocalFileSystemProvider will resolve.
        var fullPath = Path.Combine(_rootPath, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? _rootPath);
        await File.WriteAllBytesAsync(fullPath, content);

        await using var sp = BuildScopedDb();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();

        var file = new FileItem
        {
            TenantId = factory.AdminTenantId,
            DriveId = _driveId,
            Name = name,
            Path = path,
            Size = content.LongLength,
            StorageKey = path,
            IsDirectory = false,
            CreatedBy = factory.AdminUserId,
            MimeType = "text/plain",
            DeletedAt = isDeleted ? DateTimeOffset.UtcNow : null,
        };
        db.Files.Add(file);
        await db.SaveChangesAsync();
    }

    private async Task SeedFolderAsync(string name, string path)
    {
        Directory.CreateDirectory(Path.Combine(_rootPath, path));

        await using var sp = BuildScopedDb();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();

        db.Files.Add(new FileItem
        {
            TenantId = factory.AdminTenantId,
            DriveId = _driveId,
            Name = name,
            Path = path,
            Size = 0,
            IsDirectory = true,
            CreatedBy = factory.AdminUserId,
        });
        await db.SaveChangesAsync();
    }

    private ServiceProvider BuildScopedDb()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Strg.Infrastructure.Data.ITenantContext>(new FixtureTenantContext(factory.AdminTenantId));
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(factory.ConnectionString).UseOpenIddict());
        return services.BuildServiceProvider();
    }

    private sealed class FixtureTenantContext(Guid tenantId) : Strg.Infrastructure.Data.ITenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }
}
