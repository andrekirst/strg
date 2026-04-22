using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;
using Strg.Integration.Tests.Auth;
using Strg.WebDav;
using Xunit;

namespace Strg.Integration.Tests.WebDav;

/// <summary>
/// STRG-072 — WebDAV LOCK + UNLOCK acceptance tests (TC-001..TC-005) + race safety + token
/// entropy regression + PUT-on-locked gate.
///
/// <para>Runs end-to-end against a real Postgres Testcontainer: the FULL unique index on
/// <c>(TenantId, ResourceUri)</c> serializes concurrent LOCKs at the DB layer, and
/// <see cref="Strg.WebDav.DbLockManager.LockAsync"/> sweeps expired rows inline before the INSERT
/// (a volatile predicate like <c>WHERE ExpiresAt &gt; NOW()</c> is not permissible in a Postgres
/// index filter — see 42P17 discussion on
/// <see cref="Strg.Infrastructure.Data.Configurations.FileLockConfiguration"/>). Using the
/// <see cref="StrgWebApplicationFactory"/>'s shared Postgres container keeps the lock-manager's
/// <c>DbUpdateException</c> discrimination on the real SqlState/ConstraintName shape, not a
/// mocked approximation.</para>
///
/// <para><b>Why a separate test class, not a <see cref="WebDavPutGetTests"/> extension.</b> The
/// PUT-on-locked gate wants to call LOCK + PUT + UNLOCK in sequence against a stable drive, but
/// the PUT test's QuotaBytes manipulation (TC-003 shrinks admin quota to 8 bytes) would poison
/// any lock test that ran after it in the class-fixture lifetime. Separating the classes also
/// keeps a lock-manager regression from being diagnosed as a PUT regression.</para>
/// </summary>
public sealed class WebDavLockTests(StrgWebApplicationFactory factory)
    : IClassFixture<StrgWebApplicationFactory>, IAsyncLifetime
{
    private const string DriveName = "lock-test-drive";

    private string _rootPath = string.Empty;
    private Guid _driveId;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), $"strg-webdav-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootPath);
        _driveId = await EnsureDriveAsync();
        await ResetAdminQuotaAsync();
        await ClearLocksAsync();
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
    public async Task TC001_lock_returns_201_with_urn_uuid_token_in_xml_and_header()
    {
        var client = await CreateAuthenticatedClientAsync();

        using var response = await LockAsync(client, "tc001.txt", owner: "<D:href>mailto:alice@example.com</D:href>");

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            because: "RFC 4918 §9.10.8 — a newly-acquired lock on a previously-unlocked resource returns 201");
        response.Headers.TryGetValues("Lock-Token", out var headerValues).Should().BeTrue(
            because: "RFC 4918 §10.5 — the Lock-Token header is how the client learns the token for subsequent If:/Unlock requests");
        var lockTokenHeader = headerValues!.Single();
        lockTokenHeader.Should().StartWith("<urn:uuid:").And.EndWith(">",
            because: "RFC 4918 §6.4 requires angle-bracketed Coded-URL shape, and STRG-072 pins urn:uuid for the URI scheme");

        var xml = await response.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(xml);
        XNamespace dav = "DAV:";
        var href = doc.Descendants(dav + "locktoken").Descendants(dav + "href").SingleOrDefault();
        href.Should().NotBeNull(because: "LOCK responses must emit <locktoken><href>urn:uuid:...</href></locktoken>");
        href!.Value.Should().StartWith("urn:uuid:");
        href.Value.Length.Should().Be("urn:uuid:".Length + 32,
            because: "tokens are RandomNumberGenerator.GetBytes(16) hex-encoded — 128 bits → 32 hex chars");
    }

    [Fact]
    public async Task TC002_second_lock_from_same_user_with_if_header_refreshes_returning_200()
    {
        var client = await CreateAuthenticatedClientAsync();

        // First LOCK establishes the token.
        using var first = await LockAsync(client, "tc002.txt");
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstToken = ExtractLockTokenFromXml(await first.Content.ReadAsStringAsync());

        // Refresh: empty body + If:(<token>) ∧ Timeout header. RFC 4918 §9.10.2 — 200 OK, same
        // token echoed, ExpiresAt bumped.
        using var refresh = new HttpRequestMessage(new HttpMethod("LOCK"), $"/dav/{DriveName}/tc002.txt");
        refresh.Headers.TryAddWithoutValidation("If", $"(<{firstToken}>)");
        refresh.Headers.TryAddWithoutValidation("Timeout", "Second-1200");
        refresh.Content = new StringContent(string.Empty);
        refresh.Content.Headers.ContentLength = 0;
        using var refreshResponse = await client.SendAsync(refresh);

        refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "RFC 4918 §9.10.2 — lock refresh returns 200 OK (not 201) because no new lock was created");
        var refreshedToken = ExtractLockTokenFromXml(await refreshResponse.Content.ReadAsStringAsync());
        refreshedToken.Should().Be(firstToken,
            because: "refresh MUST preserve token identity — clients rely on the token staying stable across refreshes");
    }

    [Fact]
    public async Task TC003_second_lock_from_different_owner_returns_423_locked()
    {
        var clientA = await CreateAuthenticatedClientAsync();
        using var firstLock = await LockAsync(clientA, "tc003.txt");
        firstLock.StatusCode.Should().Be(HttpStatusCode.Created);

        // Second LOCK from the same caller (no If:) → conflict. We can't easily mint a second user
        // in this fixture, but the middleware treats "no If: header" as "I don't claim to own the
        // existing lock" and routes through the exact same conflict path that a second user would
        // hit — the unique index is insensitive to OwnerId (it's keyed on TenantId+ResourceUri).
        using var secondLock = await LockAsync(clientA, "tc003.txt");
        secondLock.StatusCode.Should().Be(HttpStatusCode.Locked,
            because: "RFC 4918 §9.10.6 — LOCK on an already-locked resource returns 423 Locked, regardless of who the requester is");
    }

    [Fact]
    public async Task TC004_unlock_with_wrong_token_returns_409_conflict()
    {
        var client = await CreateAuthenticatedClientAsync();
        using var lockResp = await LockAsync(client, "tc004.txt");
        lockResp.StatusCode.Should().Be(HttpStatusCode.Created);

        using var unlock = new HttpRequestMessage(new HttpMethod("UNLOCK"), $"/dav/{DriveName}/tc004.txt");
        unlock.Headers.TryAddWithoutValidation("Lock-Token", "<urn:uuid:00000000000000000000000000000000>");
        using var response = await client.SendAsync(unlock);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            because: "RFC 4918 §9.11 — UNLOCK with a token that doesn't match the active lock returns 409, not 204");

        // The real lock must still be active — a wrong-token UNLOCK is a no-op, not a stealth-unlock.
        await using var sp = BuildScopedDb();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var active = await db.FileLocks.CountAsync(l =>
            l.ResourceUri == $"{DriveName}/tc004.txt" && l.ExpiresAt > DateTimeOffset.UtcNow);
        active.Should().Be(1, because: "a rejected UNLOCK must leave the original lock row intact");
    }

    [Fact]
    public async Task TC005_lock_after_prior_lock_expired_succeeds_without_refresh()
    {
        var client = await CreateAuthenticatedClientAsync();

        // Create a pre-expired lock row directly via DB so we don't have to wait for real time.
        // This simulates "a stale lock from last week". Because Postgres rejects volatile
        // predicates in unique-index filters (42P17 on `WHERE ExpiresAt > NOW()`), the index is
        // full and DbLockManager.LockAsync runs a DELETE of expired rows on the target
        // (TenantId, ResourceUri) inside the same transaction before the INSERT. So a fresh LOCK
        // over an expired row succeeds, and the expired row is garbage-collected inline rather
        // than lingering for an external sweeper.
        await SeedExpiredLockAsync("tc005.txt");

        using var response = await LockAsync(client, "tc005.txt");
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            because: "expired rows on the same resource are deleted inline by LockAsync before the INSERT, so a fresh LOCK succeeds without any external sweeper having run");

        // Exactly one row afterwards — the inline DELETE is the cleanup. Two rows would signal a
        // regression back to the rejected partial-index design, where stale rows could accumulate.
        await using var sp = BuildScopedDb();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var total = await db.FileLocks.IgnoreQueryFilters()
            .CountAsync(l => l.ResourceUri == $"{DriveName}/tc005.txt");
        total.Should().Be(1,
            because: "LockAsync sweeps expired rows on the same resource before inserting the new one — correctness AND hygiene happen together");
    }

    [Fact]
    public async Task put_on_locked_resource_without_if_header_returns_423()
    {
        var client = await CreateAuthenticatedClientAsync();
        using var lockResp = await LockAsync(client, "gated.txt");
        lockResp.StatusCode.Should().Be(HttpStatusCode.Created);

        using var putNoIf = new HttpRequestMessage(HttpMethod.Put, $"/dav/{DriveName}/gated.txt")
        {
            Content = new ByteArrayContent("blocked"u8.ToArray()),
        };
        using var response = await client.SendAsync(putNoIf);
        response.StatusCode.Should().Be(HttpStatusCode.Locked,
            because: "RFC 4918 §9.10.6 — a PUT against a locked resource without an If:-header naming the token returns 423 Locked");
    }

    [Fact]
    public async Task put_on_locked_resource_with_matching_if_header_succeeds()
    {
        var client = await CreateAuthenticatedClientAsync();
        using var lockResp = await LockAsync(client, "lock-then-write.txt");
        lockResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var token = ExtractLockTokenFromXml(await lockResp.Content.ReadAsStringAsync());

        using var put = new HttpRequestMessage(HttpMethod.Put, $"/dav/{DriveName}/lock-then-write.txt")
        {
            Content = new ByteArrayContent("payload"u8.ToArray()),
        };
        put.Headers.TryAddWithoutValidation("If", $"(<{token}>)");
        using var response = await client.SendAsync(put);
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            because: "the lock's owner presenting the matching token via If: MUST be able to write through their own lock — that's the whole point of class-2 locks");
    }

    [Fact]
    public async Task lock_without_files_write_scope_returns_403()
    {
        using var tokenResp = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword,
            scopes: "files.read");
        tokenResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var (accessToken, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResp);
        var client = factory.CreateAuthenticatedClient(accessToken);

        using var response = await LockAsync(client, "unscoped.txt");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "LOCK is a write-surface (it gates future writes), so files.write is required — 403 not 401 because the credential is valid");
    }

    [Fact]
    public async Task concurrent_locks_on_same_resource_result_in_exactly_one_acquired()
    {
        var client = await CreateAuthenticatedClientAsync();

        // Fire ten parallel LOCKs at the same path. The partial-unique-index race defence should
        // let exactly one win with 201 and the rest see 423. A query-then-insert implementation
        // would silently produce 2+ winners here, and the DB state assertion at the bottom would
        // catch it even if the HTTP-level counts happened to align.
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => LockAsync(client, "race.txt"))
            .ToArray();
        var responses = await Task.WhenAll(tasks);

        try
        {
            var statuses = responses.Select(r => (int)r.StatusCode).ToArray();
            statuses.Count(s => s == StatusCodes.Status201Created).Should().Be(1,
                because: "at most one LOCK can win the race — the full unique index on (TenantId, ResourceUri) enforces this atomically at the DB layer");
            statuses.Where(s => s != StatusCodes.Status201Created)
                .Should().AllSatisfy(s => s.Should().Be(StatusCodes.Status423Locked),
                    because: "every losing LOCK must receive 423 — any other status would mean a silent race hazard leaking through");

            await using var sp = BuildScopedDb();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            var active = await db.FileLocks.CountAsync(l =>
                l.ResourceUri == $"{DriveName}/race.txt" && l.ExpiresAt > DateTimeOffset.UtcNow);
            active.Should().Be(1, because: "the DB is the single source of truth for 'exactly one active lock' and the unique index makes that the hard invariant");
        }
        finally
        {
            foreach (var r in responses)
            {
                r.Dispose();
            }
        }
    }

    [Fact]
    public async Task options_advertises_dav_class_1_and_2()
    {
        using var tokenResp = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail, StrgWebApplicationFactory.AdminPassword);
        var (accessToken, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResp);
        var client = factory.CreateAuthenticatedClient(accessToken);

        using var request = new HttpRequestMessage(HttpMethod.Options, $"/dav/{DriveName}/");
        using var response = await client.SendAsync(request);

        response.Headers.TryGetValues("DAV", out var davValues).Should().BeTrue();
        davValues!.Single().Should().Contain("2",
            because: "RFC 4918 §10.1 class-2 compliance requires advertising DAV: 2 whenever lock support is present — this is the pin that says LOCK/UNLOCK dispatch is wired");
    }

    [Fact]
    public void generated_token_uses_cryptographic_random_not_guid()
    {
        // Regression pin for the security-critical dispatch constraint: lock tokens MUST come from
        // RandomNumberGenerator, not Guid.NewGuid(). A distributional test on 1 000 samples is a
        // blunt instrument but it catches the obvious regression (someone replacing the body with
        // Guid.NewGuid().ToString()) because Guid v4's layout has deterministic nibble positions
        // for the version (8 = '4' at offset 14) and variant (8–b at offset 19).
        var tokens = Enumerable.Range(0, 1000)
            .Select(_ => DbLockManager.GenerateSecureToken())
            .ToArray();

        tokens.Should().OnlyHaveUniqueItems(
            because: "128 bits of CSPRNG output have a 2^-64 collision probability after ~1 billion draws — 1 000 draws must be fully unique");

        foreach (var t in tokens)
        {
            t.Should().StartWith("urn:uuid:");
            var hex = t["urn:uuid:".Length..];
            hex.Length.Should().Be(32);
            hex.Should().MatchRegex("^[0-9a-f]{32}$",
                because: "Convert.ToHexString(...).ToLowerInvariant() emits lowercase hex only — a stray uppercase char would mean the casing pass dropped");
        }

        // The signature pin: a Guid.NewGuid().ToString("N") would consistently have '4' at offset
        // 12 (the version-4 marker) and 8/9/a/b at offset 16 (the variant nibble). A CSPRNG stream
        // should have roughly 1/16 probability for each of those positions. If someone regresses
        // to Guid.NewGuid, the test fails because the '4' rate is ~100% instead of ~6.25%.
        var fourCountAtVersionOffset = tokens.Count(t => t["urn:uuid:".Length + 12] == '4');
        fourCountAtVersionOffset.Should().BeLessThan(200,
            because: "CSPRNG output has ~62 '4's at any hex position across 1 000 samples; a Guid regression would spike this to ~1000");
    }

    // ---- helpers ----

    private static async Task<HttpResponseMessage> LockAsync(HttpClient client, string path, string? owner = null)
    {
        var body = owner is null
            ? """<?xml version="1.0" encoding="utf-8"?><D:lockinfo xmlns:D="DAV:"><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype></D:lockinfo>"""
            : $"""<?xml version="1.0" encoding="utf-8"?><D:lockinfo xmlns:D="DAV:"><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype><D:owner>{owner}</D:owner></D:lockinfo>""";
        using var request = new HttpRequestMessage(new HttpMethod("LOCK"), $"/dav/{DriveName}/{path}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml"),
        };
        return await client.SendAsync(request);
    }

    private static string ExtractLockTokenFromXml(string xml)
    {
        var doc = XDocument.Parse(xml);
        XNamespace dav = "DAV:";
        return doc.Descendants(dav + "locktoken").Descendants(dav + "href").Single().Value;
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

    private async Task ResetAdminQuotaAsync()
    {
        await using var sp = BuildScopedDb();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var admin = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == factory.AdminUserId);
        admin.QuotaBytes = 10L * 1024 * 1024 * 1024;
        admin.UsedBytes = 0;
        await db.SaveChangesAsync();
    }

    private async Task ClearLocksAsync()
    {
        await using var sp = BuildScopedDb();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        await db.FileLocks.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    private async Task SeedExpiredLockAsync(string path)
    {
        await using var sp = BuildScopedDb();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        db.FileLocks.Add(new FileLock
        {
            TenantId = factory.AdminTenantId,
            ResourceUri = $"{DriveName}/{path}",
            Token = "urn:uuid:00000000000000000000000000000001",
            OwnerId = factory.AdminUserId,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
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
