using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;
using Strg.Integration.Tests.Auth;
using Xunit;

namespace Strg.Integration.Tests.WebDav;

/// <summary>
/// STRG-069 — PROPFIND property mapping, Depth handling, custom <c>strg:</c> namespace dead
/// properties, and the Depth:infinity DoS cap. TC-001..TC-006 pin the wire-level shape end-to-end.
///
/// <para>These tests share the <see cref="StrgWebApplicationFactory"/> with the rest of the
/// integration suite — real Postgres + real OpenIddict + real middleware pipeline. A per-test-class
/// temp directory holds the LocalFileSystemProvider <c>rootPath</c>; FileItems are seeded directly
/// through <see cref="StrgDbContext"/> (same pattern as <see cref="WebDavStoreTests"/>).</para>
///
/// <para><b>TC-006 uses its own factory</b> via <see cref="WebApplicationFactoryExtensions.WithWebHostBuilder"/>
/// so a small <c>PropfindInfinityMaxItems</c> cap can be injected without affecting other tests.
/// Trying to override the cap on the shared factory would leak into parallel tests running under
/// the same class fixture — the isolated factory instance is the clean way to pin the 507 shape.</para>
/// </summary>
public sealed class WebDavPropFindTests(StrgWebApplicationFactory factory)
    : IClassFixture<StrgWebApplicationFactory>, IAsyncLifetime
{
    private const string DriveName = "propfind-test-drive";
    private const string DavNs = "DAV:";
    private const string StrgNs = "urn:strg:webdav";

    private string _rootPath = string.Empty;
    private Guid _driveId;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), $"strg-propfind-{Guid.NewGuid():N}");
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
    public async Task TC001_propfind_depth1_lists_children_with_correct_hrefs()
    {
        await SeedFileAsync(name: "alpha.txt", path: "alpha.txt", content: "a"u8.ToArray());
        await SeedFileAsync(name: "beta.txt", path: "beta.txt", content: "b"u8.ToArray());
        await SeedFolderAsync(name: "nested", path: "nested");

        var client = await CreateAuthenticatedClientAsync(factory);

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"/dav/{DriveName}/");
        request.Headers.Add("Depth", "1");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.MultiStatus);
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var hrefs = doc.Descendants(XName.Get("href", DavNs))
            .Select(e => e.Value)
            .ToList();

        hrefs.Should().Contain(h => h.EndsWith("/alpha.txt", StringComparison.Ordinal));
        hrefs.Should().Contain(h => h.EndsWith("/beta.txt", StringComparison.Ordinal));
        hrefs.Should().Contain(h => h.EndsWith("/nested", StringComparison.Ordinal)
            || h.EndsWith("/nested/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TC002_resourcetype_marks_folders_with_collection_element_and_files_with_empty()
    {
        await SeedFileAsync(name: "doc.txt", path: "doc.txt", content: "x"u8.ToArray());
        await SeedFolderAsync(name: "folder", path: "folder");

        var client = await CreateAuthenticatedClientAsync(factory);

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"/dav/{DriveName}/");
        request.Headers.Add("Depth", "1");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.MultiStatus);
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());

        // Files: resourcetype present but empty — no <D:collection/> child.
        var fileResponse = FindResponseByHref(doc, "/doc.txt");
        var fileResourceType = fileResponse.Descendants(XName.Get("resourcetype", DavNs)).Single();
        fileResourceType.Element(XName.Get("collection", DavNs)).Should().BeNull(
            because: "RFC 4918 §14.10 — non-collection resources emit an empty resourcetype element");

        // Folders: resourcetype contains <D:collection/> — the marker WebDAV clients render as folder icons.
        var folderResponse = FindResponseByHref(doc, "/folder");
        var folderResourceType = folderResponse.Descendants(XName.Get("resourcetype", DavNs)).Single();
        folderResourceType.Element(XName.Get("collection", DavNs)).Should().NotBeNull(
            because: "RFC 4918 §14.10 — collection resources MUST emit <D:collection/> inside resourcetype");
    }

    [Fact]
    public async Task TC003_depth_infinity_under_cap_returns_all_descendants()
    {
        await SeedFolderAsync(name: "d1", path: "d1");
        await SeedFileAsync(name: "f1.txt", path: "d1/f1.txt", content: "1"u8.ToArray());
        await SeedFolderAsync(name: "d2", path: "d1/d2");
        await SeedFileAsync(name: "f2.txt", path: "d1/d2/f2.txt", content: "2"u8.ToArray());
        await SeedFileAsync(name: "f3.txt", path: "d1/d2/f3.txt", content: "3"u8.ToArray());

        var client = await CreateAuthenticatedClientAsync(factory);

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"/dav/{DriveName}/");
        request.Headers.Add("Depth", "infinity");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.MultiStatus);
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var hrefs = doc.Descendants(XName.Get("href", DavNs)).Select(e => e.Value).ToList();

        // Root + 5 descendants = 6 responses — all seeded items must appear irrespective of depth.
        hrefs.Should().Contain(h => h.EndsWith("/d1", StringComparison.Ordinal) || h.EndsWith("/d1/", StringComparison.Ordinal));
        hrefs.Should().Contain(h => h.EndsWith("/d1/f1.txt", StringComparison.Ordinal));
        hrefs.Should().Contain(h => h.EndsWith("/d1/d2", StringComparison.Ordinal) || h.EndsWith("/d1/d2/", StringComparison.Ordinal));
        hrefs.Should().Contain(h => h.EndsWith("/d1/d2/f2.txt", StringComparison.Ordinal));
        hrefs.Should().Contain(h => h.EndsWith("/d1/d2/f3.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TC004_getetag_is_quoted_content_hash()
    {
        // 64 hex chars — matches the FileItem.ContentHash column cap. In production this is a full
        // sha256 digest; the prefix-free form is the on-disk convention across the codebase.
        const string hash = "5f4dcc3b5aa765d61d8327deb882cf992f4dcc3b5aa765d61d8327deb882cf99";
        await SeedFileAsync(name: "etagged.txt", path: "etagged.txt", content: "hello"u8.ToArray(),
            contentHash: hash);

        var client = await CreateAuthenticatedClientAsync(factory);

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"/dav/{DriveName}/etagged.txt");
        request.Headers.Add("Depth", "0");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.MultiStatus);
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var etag = doc.Descendants(XName.Get("getetag", DavNs)).Single().Value;

        // RFC 7232 §2.3 — entity-tag = [ weak ] opaque-tag ; opaque-tag = DQUOTE *etagc DQUOTE.
        // Without the double-quotes, clients that do syntactic ETag parsing skip cache revalidation.
        etag.Should().Be($"\"{hash}\"",
            because: "RFC 7232 §2.3 requires ETags to be DQUOTE-wrapped; unquoted hashes break cache revalidation");
    }

    [Fact]
    public async Task TC005_strg_contenthash_dead_property_is_emitted_in_custom_namespace()
    {
        const string hash = "deadbeefcafef00d1234567890abcdef0fedcba9876543210fedcba987654321";
        await SeedFileAsync(name: "hashed.txt", path: "hashed.txt", content: "hash-me"u8.ToArray(),
            contentHash: hash);

        var client = await CreateAuthenticatedClientAsync(factory);

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"/dav/{DriveName}/hashed.txt");
        request.Headers.Add("Depth", "0");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.MultiStatus);
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());

        // Dead property lookup goes through the strg namespace, NOT DAV: — clients that dedupe on
        // ContentHash can't rely on the quoted getetag shape when they need the raw hash.
        var contentHash = doc.Descendants(XName.Get("contenthash", StrgNs)).Single().Value;
        contentHash.Should().Be(hash,
            because: "strg:contenthash is the un-wrapped hash for deduplicating clients — no quotes, correct namespace");

        var version = doc.Descendants(XName.Get("version", StrgNs)).Single().Value;
        version.Should().Be("1", because: "FileItem.VersionCount defaults to 1 and is surfaced as strg:version");
    }

    [Fact]
    public async Task TC006_depth_infinity_over_cap_returns_507()
    {
        // Tight cap (3 total items = root + 2 descendants). Seeding 5 descendants guarantees
        // CountDescendantsBoundedAsync returns bounded=4 (Take(4)) and 4 + 1 > 3 triggers 507
        // BEFORE any XML is written. Using an isolated factory instance because a factory-wide
        // override would bleed into the other tests in the class.
        const int infinityCap = 3;
        await SeedFileAsync(name: "a.txt", path: "a.txt", content: "a"u8.ToArray());
        await SeedFileAsync(name: "b.txt", path: "b.txt", content: "b"u8.ToArray());
        await SeedFileAsync(name: "c.txt", path: "c.txt", content: "c"u8.ToArray());
        await SeedFileAsync(name: "d.txt", path: "d.txt", content: "d"u8.ToArray());
        await SeedFileAsync(name: "e.txt", path: "e.txt", content: "e"u8.ToArray());

        await using var cappedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WebDav:PropfindInfinityMaxItems"] = infinityCap.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
            });
        });

        // `WithWebHostBuilder` spins up a fresh host with its own ephemeral OpenIddict signing
        // keys, so a token minted on the outer factory fails JWT signature validation here. Mint
        // the token through the capped factory's own /connect/token so the signing key matches
        // the validator — both factories share the same Postgres so the admin user lookup still
        // resolves identically.
        var tokenClient = cappedFactory.CreateDefaultClient();
        using var tokenResponse = await tokenClient.PostAsync("/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = StrgWebApplicationFactory.AdminEmail,
                ["password"] = StrgWebApplicationFactory.AdminPassword,
                ["client_id"] = StrgWebApplicationFactory.DefaultClientId,
                ["scope"] = StrgWebApplicationFactory.AdminScopes,
            }));
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var (accessToken, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);

        var client = cappedFactory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"/dav/{DriveName}/");
        request.Headers.Add("Depth", "infinity");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be((HttpStatusCode)507,
            because: "Depth:infinity exceeding PropfindInfinityMaxItems must short-circuit with 507 Insufficient Storage before any XML is written");
    }

    // ---- helpers ----

    private static XElement FindResponseByHref(XDocument doc, string hrefSuffix) =>
        doc.Descendants(XName.Get("response", DavNs))
            .Single(r => r.Element(XName.Get("href", DavNs))?.Value?.EndsWith(hrefSuffix, StringComparison.Ordinal) == true
                         || r.Element(XName.Get("href", DavNs))?.Value?.EndsWith(hrefSuffix + "/", StringComparison.Ordinal) == true);

    private async Task<HttpClient> CreateAuthenticatedClientAsync(StrgWebApplicationFactory fac)
    {
        var accessToken = await AcquireAccessTokenAsync();
        return fac.CreateAuthenticatedClient(accessToken);
    }

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

    private async Task SeedFileAsync(string name, string path, byte[] content, string? contentHash = null)
    {
        var fullPath = Path.Combine(_rootPath, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? _rootPath);
        await File.WriteAllBytesAsync(fullPath, content);

        await using var sp = BuildScopedDb();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();

        db.Files.Add(new FileItem
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
            ContentHash = contentHash,
        });
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
        services.AddSingleton<ITenantContext>(new FixtureTenantContext(factory.AdminTenantId));
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(factory.ConnectionString).UseOpenIddict());
        return services.BuildServiceProvider();
    }

    private sealed class FixtureTenantContext(Guid tenantId) : ITenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }
}
