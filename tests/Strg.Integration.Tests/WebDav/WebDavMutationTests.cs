using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;
using Strg.Integration.Tests.Auth;
using Xunit;

namespace Strg.Integration.Tests.WebDav;

/// <summary>
/// STRG-071 — WebDAV MKCOL/DELETE/COPY/MOVE acceptance tests (TC-001..TC-005) plus the security
/// + code-review checklist pins. Runs end-to-end through the real ASP.NET Core pipeline, the
/// real Mediator, the real handlers (DeleteFileHandler/CopyFileHandler/MoveFileHandler/
/// CreateFolderHandler) and the real PostgreSQL Testcontainer.
///
/// <para><b>Why a new suite, not extending <see cref="WebDavPutGetTests"/>.</b> PUT-GET pins the
/// upload + read round-trip. Mutations are a different failure surface (recursive soft-delete,
/// path-rebase under directory MOVE, RFC 4918 §10.3 Destination header parsing,
/// <c>Overwrite: F</c> precondition). Conflating both into one suite means a regression in either
/// direction produces a single confusing "WebDavPutGetTests broke" signal — the dedicated class
/// keeps the diagnosis surface narrow.</para>
///
/// <para><b>Two-drive fixture for cross-drive pins.</b> The cross-drive 502 test (a security-review
/// requirement) needs a SECOND drive owned by the same admin tenant — without it, a Destination
/// pointing at <c>/dav/other-drive/...</c> is indistinguishable from a malformed-path 400. The
/// fixture seeds both drives and exposes their names so the tests can address both.</para>
/// </summary>
public sealed class WebDavMutationTests(StrgWebApplicationFactory factory)
    : IClassFixture<StrgWebApplicationFactory>, IAsyncLifetime
{
    private const string DriveName = "mutation-test-drive";
    private const string OtherDriveName = "mutation-test-other-drive";

    private string _rootPath = string.Empty;
    private string _otherRootPath = string.Empty;
    private Guid _driveId;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), $"strg-webdav-mut-{Guid.NewGuid():N}");
        _otherRootPath = Path.Combine(Path.GetTempPath(), $"strg-webdav-mut-other-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootPath);
        Directory.CreateDirectory(_otherRootPath);
        _driveId = await EnsureDriveAsync(DriveName, _rootPath);
        await EnsureDriveAsync(OtherDriveName, _otherRootPath);
        // Restore admin's quota so a previous test in the class fixture's lifetime can't
        // poison COPY's quota commit path (CopyFileHandler runs CommitAsync on every copy).
        await ResetAdminQuotaAsync(quotaBytes: 10L * 1024 * 1024 * 1024, usedBytes: 0);
    }

    Task IAsyncLifetime.DisposeAsync()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
        if (Directory.Exists(_otherRootPath))
        {
            Directory.Delete(_otherRootPath, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TC001_MkCol_creates_directory_visible_in_PropFind()
    {
        var client = await CreateAuthenticatedClientAsync();
        var folderName = $"newdir-{Guid.NewGuid():N}";

        using var mkcol = new HttpRequestMessage(new HttpMethod("MKCOL"), $"/dav/{DriveName}/{folderName}/");
        using var mkcolResponse = await client.SendAsync(mkcol);
        mkcolResponse.StatusCode.Should().Be(HttpStatusCode.Created,
            because: "RFC 4918 §9.3.1 — MKCOL on a non-existing target with an existing parent returns 201");

        // PROPFIND on the parent (drive root) at depth 1 must list the new collection. Reading
        // the XML body and looking for the folder name is the wire-level pin — checking the DB
        // directly would not catch a regression where the row exists but PROPFIND filters it.
        using var propfind = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"/dav/{DriveName}/");
        propfind.Headers.Add("Depth", "1");
        using var propfindResponse = await client.SendAsync(propfind);
        propfindResponse.StatusCode.Should().Be(HttpStatusCode.MultiStatus);
        var body = await propfindResponse.Content.ReadAsStringAsync();
        body.Should().Contain(folderName,
            because: "the freshly-created collection MUST appear in a Depth:1 PROPFIND of its parent — that's the AC's user-visible signal");
    }

    [Fact]
    public async Task MkCol_with_request_body_returns_415_UnsupportedMediaType()
    {
        // RFC 4918 §9.3.1 — request body is reserved. We refuse non-empty bodies up-front so
        // a future spec extension that defines a body schema doesn't get silently ignored by
        // an older server.
        var client = await CreateAuthenticatedClientAsync();
        using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), $"/dav/{DriveName}/with-body/")
        {
            Content = new StringContent("non-empty", Encoding.UTF8, "text/plain"),
        };
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task MkCol_on_existing_path_returns_405_MethodNotAllowed()
    {
        var client = await CreateAuthenticatedClientAsync();
        var folderName = $"existing-{Guid.NewGuid():N}";

        // First MKCOL succeeds.
        using (var first = new HttpRequestMessage(new HttpMethod("MKCOL"), $"/dav/{DriveName}/{folderName}/"))
        {
            using var firstResp = await client.SendAsync(first);
            firstResp.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        // Second MKCOL must 405 (RFC 4918 §9.3.1 — collection or resource already exists).
        using var second = new HttpRequestMessage(new HttpMethod("MKCOL"), $"/dav/{DriveName}/{folderName}/");
        using var secondResp = await client.SendAsync(second);
        secondResp.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task TC002_Delete_file_excludes_from_PropFind()
    {
        var client = await CreateAuthenticatedClientAsync();
        var fileName = $"to-delete-{Guid.NewGuid():N}.txt";
        await PutAsync(client, fileName, Encoding.UTF8.GetBytes("payload"));

        using var del = new HttpRequestMessage(HttpMethod.Delete, $"/dav/{DriveName}/{fileName}");
        using var delResponse = await client.SendAsync(del);
        delResponse.StatusCode.Should().Be(HttpStatusCode.NoContent,
            because: "RFC 4918 §9.6 — DELETE on an existing resource returns 204 No Content");

        // PROPFIND parent at depth 1; the deleted file's name MUST NOT appear. Reading the body
        // string-contains is fragile only against accidental substring collisions — the test name
        // includes a Guid so it can't collide with the seeded admin/other test artefacts.
        using var propfind = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"/dav/{DriveName}/");
        propfind.Headers.Add("Depth", "1");
        using var propfindResponse = await client.SendAsync(propfind);
        var body = await propfindResponse.Content.ReadAsStringAsync();
        body.Should().NotContain(fileName,
            because: "soft-deleted FileItems are filtered by the global DeletedAt query filter — they MUST NOT surface in PROPFIND");

        // GET on the deleted path must 404. This is the user-observable contract for the AC line
        // "file no longer in PROPFIND" — the wire shape of "gone" is 404, not an empty 200.
        using var get = await client.GetAsync($"/dav/{DriveName}/{fileName}");
        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TC003_Delete_directory_with_three_children_softdeletes_all_four()
    {
        var client = await CreateAuthenticatedClientAsync();
        var dir = $"tree-{Guid.NewGuid():N}";

        // Seed: directory + three files inside it.
        using (var mkcol = new HttpRequestMessage(new HttpMethod("MKCOL"), $"/dav/{DriveName}/{dir}/"))
        {
            using var resp = await client.SendAsync(mkcol);
            resp.StatusCode.Should().Be(HttpStatusCode.Created);
        }
        await PutAsync(client, $"{dir}/a.txt", Encoding.UTF8.GetBytes("alpha"));
        await PutAsync(client, $"{dir}/b.txt", Encoding.UTF8.GetBytes("beta"));
        await PutAsync(client, $"{dir}/c.txt", Encoding.UTF8.GetBytes("gamma"));

        // DELETE the directory; recursive soft-delete is DeleteFileHandler's job.
        using var del = new HttpRequestMessage(HttpMethod.Delete, $"/dav/{DriveName}/{dir}/");
        using var delResponse = await client.SendAsync(del);
        delResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // DB-level pin: all four rows have DeletedAt set. Querying with IgnoreQueryFilters because
        // the global filter would otherwise mask soft-deleted rows from view — the whole point of
        // this test is to verify the rows ARE marked deleted.
        await using var sp = BuildScopedDb();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var rows = await db.Files
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.DriveId == _driveId &&
                        (f.Path == dir || f.Path.StartsWith(dir + "/")))
            .ToListAsync();
        rows.Should().HaveCount(4);
        rows.Should().OnlyContain(r => r.DeletedAt != null,
            because: "the AC pins recursive soft-delete on every descendant — leaving any row alive would orphan it under a deleted parent");
    }

    [Fact]
    public async Task Delete_directory_with_prefix_collision_does_not_softdelete_sibling()
    {
        // Code-review checklist pin: Path.StartsWith() for recursive child matching uses '/' suffix
        // (prevents prefix collision). DeleteFileHandler uses prefix + "/" so deleting "docs/"
        // does NOT cascade to "docsbackup/". A regression that forgot the trailing slash would
        // soft-delete every sibling whose name starts with "docs".
        var client = await CreateAuthenticatedClientAsync();
        var siblingPrefix = $"docs-{Guid.NewGuid():N}";
        var collisionPrefix = $"{siblingPrefix}backup";

        using (var mk1 = new HttpRequestMessage(new HttpMethod("MKCOL"), $"/dav/{DriveName}/{siblingPrefix}/"))
        {
            using var r = await client.SendAsync(mk1);
            r.StatusCode.Should().Be(HttpStatusCode.Created);
        }
        using (var mk2 = new HttpRequestMessage(new HttpMethod("MKCOL"), $"/dav/{DriveName}/{collisionPrefix}/"))
        {
            using var r = await client.SendAsync(mk2);
            r.StatusCode.Should().Be(HttpStatusCode.Created);
        }
        await PutAsync(client, $"{siblingPrefix}/inside.txt", Encoding.UTF8.GetBytes("inside"));
        await PutAsync(client, $"{collisionPrefix}/safe.txt", Encoding.UTF8.GetBytes("safe"));

        using var del = new HttpRequestMessage(HttpMethod.Delete, $"/dav/{DriveName}/{siblingPrefix}/");
        using var delResp = await client.SendAsync(del);
        delResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var sp = BuildScopedDb();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var collisionRow = await db.Files
            .AsNoTracking()
            .SingleAsync(f => f.DriveId == _driveId && f.Path == $"{collisionPrefix}/safe.txt");
        collisionRow.DeletedAt.Should().BeNull(
            because: "the trailing-slash anchor in DeleteFileHandler's StartsWith filter prevents `docs/` " +
                     "from sweeping `docsbackup/safe.txt` — without it the prefix collision would silently " +
                     "soft-delete unrelated files");
    }

    [Fact]
    public async Task TC004_Copy_with_Overwrite_F_returns_412_when_destination_exists()
    {
        var client = await CreateAuthenticatedClientAsync();
        var src = $"copy-src-{Guid.NewGuid():N}.txt";
        var dst = $"copy-dst-{Guid.NewGuid():N}.txt";
        await PutAsync(client, src, Encoding.UTF8.GetBytes("source"));
        await PutAsync(client, dst, Encoding.UTF8.GetBytes("blocker"));

        using var copy = new HttpRequestMessage(new HttpMethod("COPY"), $"/dav/{DriveName}/{src}");
        copy.Headers.Add("Destination", $"/dav/{DriveName}/{dst}");
        copy.Headers.Add("Overwrite", "F");
        using var response = await client.SendAsync(copy);

        response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed,
            because: "RFC 4918 §10.6 / §9.8.5 — Overwrite: F + existing destination MUST return 412 PreconditionFailed");
    }

    [Fact]
    public async Task Copy_to_fresh_destination_returns_201_with_Location_header()
    {
        var client = await CreateAuthenticatedClientAsync();
        var src = $"copy-fresh-src-{Guid.NewGuid():N}.txt";
        var dst = $"copy-fresh-dst-{Guid.NewGuid():N}.txt";
        await PutAsync(client, src, Encoding.UTF8.GetBytes("source-bytes"));

        using var copy = new HttpRequestMessage(new HttpMethod("COPY"), $"/dav/{DriveName}/{src}");
        copy.Headers.Add("Destination", $"/dav/{DriveName}/{dst}");
        using var response = await client.SendAsync(copy);

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            because: "RFC 4918 §9.8.5 — COPY to a previously non-existent destination returns 201");
        response.Headers.Location.Should().NotBeNull(
            because: "Location header points the client at the new resource — required for clients that follow the link");
        response.Headers.Location!.ToString().Should().Be($"/dav/{DriveName}/{dst}");

        // Both source and destination must be readable; COPY is purely additive.
        using var srcGet = await client.GetAsync($"/dav/{DriveName}/{src}");
        srcGet.StatusCode.Should().Be(HttpStatusCode.OK);
        using var dstGet = await client.GetAsync($"/dav/{DriveName}/{dst}");
        dstGet.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TC005_Move_old_path_404_new_path_200_with_byte_identical_body()
    {
        var client = await CreateAuthenticatedClientAsync();
        var src = $"move-src-{Guid.NewGuid():N}.txt";
        var dst = $"move-dst-{Guid.NewGuid():N}.txt";
        var payload = Encoding.UTF8.GetBytes("move-payload — byte-identical round trip");
        await PutAsync(client, src, payload);

        using var move = new HttpRequestMessage(new HttpMethod("MOVE"), $"/dav/{DriveName}/{src}");
        move.Headers.Add("Destination", $"/dav/{DriveName}/{dst}");
        using var moveResponse = await client.SendAsync(move);
        moveResponse.StatusCode.Should().Be(HttpStatusCode.Created,
            because: "RFC 4918 §9.9.4 — MOVE to a previously non-existent destination returns 201");

        using var oldGet = await client.GetAsync($"/dav/{DriveName}/{src}");
        oldGet.StatusCode.Should().Be(HttpStatusCode.NotFound,
            because: "the source path MUST NOT resolve after MOVE — that's the AC's 'original path returns 404' pin");

        using var newGet = await client.GetAsync($"/dav/{DriveName}/{dst}");
        newGet.StatusCode.Should().Be(HttpStatusCode.OK);
        var newBytes = await newGet.Content.ReadAsByteArrayAsync();
        newBytes.Should().Equal(payload,
            because: "MOVE relocates the FileItem record but the underlying blob (Guid-keyed) is unchanged — bytes round-trip exactly");
    }

    [Fact]
    public async Task Move_directory_rebases_descendants_under_new_prefix()
    {
        // Code-review pin: MoveFileHandler.cs:200-225 rebases descendants via
        // FileItem.RebaseUnder(). A regression that only renamed the parent row would orphan
        // every descendant under a stale prefix (PROPFIND would still see them under the OLD
        // path). The DB-level Path assertion is the load-bearing pin here.
        var client = await CreateAuthenticatedClientAsync();
        var oldDir = $"oldroot-{Guid.NewGuid():N}";
        var newDir = $"newroot-{Guid.NewGuid():N}";

        using (var mkcol = new HttpRequestMessage(new HttpMethod("MKCOL"), $"/dav/{DriveName}/{oldDir}/"))
        {
            using var r = await client.SendAsync(mkcol);
            r.StatusCode.Should().Be(HttpStatusCode.Created);
        }
        await PutAsync(client, $"{oldDir}/leaf.txt", Encoding.UTF8.GetBytes("leaf-bytes"));
        using (var mkcolSub = new HttpRequestMessage(new HttpMethod("MKCOL"), $"/dav/{DriveName}/{oldDir}/sub/"))
        {
            using var r = await client.SendAsync(mkcolSub);
            r.StatusCode.Should().Be(HttpStatusCode.Created);
        }
        await PutAsync(client, $"{oldDir}/sub/nested.txt", Encoding.UTF8.GetBytes("nested-bytes"));

        using var move = new HttpRequestMessage(new HttpMethod("MOVE"), $"/dav/{DriveName}/{oldDir}/");
        move.Headers.Add("Destination", $"/dav/{DriveName}/{newDir}/");
        using var moveResp = await client.SendAsync(move);
        moveResp.StatusCode.Should().Be(HttpStatusCode.Created);

        await using var sp = BuildScopedDb();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();

        // Old prefix must be empty (excluding soft-deleted rows via global filter).
        var staleRows = await db.Files
            .AsNoTracking()
            .Where(f => f.DriveId == _driveId &&
                        (f.Path == oldDir || f.Path.StartsWith(oldDir + "/")))
            .ToListAsync();
        staleRows.Should().BeEmpty(
            because: "after directory MOVE, no FileItem must remain under the old prefix — RebaseUnder rewrites every descendant");

        // New prefix carries the leaf + nested files.
        var movedRows = await db.Files
            .AsNoTracking()
            .Where(f => f.DriveId == _driveId && f.Path.StartsWith(newDir + "/"))
            .Select(f => f.Path)
            .ToListAsync();
        movedRows.Should().Contain($"{newDir}/leaf.txt");
        movedRows.Should().Contain($"{newDir}/sub/nested.txt");
    }

    [Fact]
    public async Task Cross_drive_destination_returns_502_BadGateway()
    {
        // Security-review pin: Cross-drive destination → rejected. The middleware refuses with
        // 502 (RFC 4918 §9.8.5 / §9.9.4 reserved for "destination on a different server"; the
        // strg spec extends the same shape to cross-drive because WebDAV clients can't usefully
        // relocate across drives without re-authenticating to the other drive's context).
        var client = await CreateAuthenticatedClientAsync();
        var src = $"xdrive-src-{Guid.NewGuid():N}.txt";
        await PutAsync(client, src, Encoding.UTF8.GetBytes("payload"));

        using var copy = new HttpRequestMessage(new HttpMethod("COPY"), $"/dav/{DriveName}/{src}");
        copy.Headers.Add("Destination", $"/dav/{OtherDriveName}/anywhere.txt");
        using var response = await client.SendAsync(copy);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway,
            because: "WebDAV cross-drive operations are deferred to REST; the 502 tells the client " +
                     "the operation cannot be satisfied via this protocol surface (use POST /api/v1/.../copy instead)");
    }

    [Fact]
    public async Task Cross_host_destination_returns_502_BadGateway()
    {
        // Security-review pin sibling to cross-drive: a Destination on a different host must not
        // silently fall through to "treat as in-drive path". Pinning 502 ensures the parser's
        // host-comparison stays load-bearing — if a future refactor stops comparing hosts, this
        // test fails before the refusal becomes silent.
        var client = await CreateAuthenticatedClientAsync();
        var src = $"xhost-src-{Guid.NewGuid():N}.txt";
        await PutAsync(client, src, Encoding.UTF8.GetBytes("payload"));

        using var copy = new HttpRequestMessage(new HttpMethod("COPY"), $"/dav/{DriveName}/{src}");
        copy.Headers.Add("Destination", $"https://attacker.example.com/dav/{DriveName}/anywhere.txt");
        using var response = await client.SendAsync(copy);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task Invalid_destination_path_returns_400_BadRequest()
    {
        // Security-review pin: Destination header validated through StoragePath.Parse(). A
        // path-traversal payload in the Destination header MUST NOT reach IStorageProvider —
        // StoragePath.Parse rejects ".." segments, and the parser's InvalidPath status maps to
        // 400. A regression that bypassed this validator would let a malicious Destination
        // escape into the storage backend.
        var client = await CreateAuthenticatedClientAsync();
        var src = $"badpath-src-{Guid.NewGuid():N}.txt";
        await PutAsync(client, src, Encoding.UTF8.GetBytes("payload"));

        using var copy = new HttpRequestMessage(new HttpMethod("COPY"), $"/dav/{DriveName}/{src}");
        copy.Headers.Add("Destination", $"/dav/{DriveName}/../escaped.txt");
        using var response = await client.SendAsync(copy);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_without_files_write_scope_returns_403_Forbidden()
    {
        // Security-review pin: DELETE requires files.write scope. Mint a token with only
        // files.read; the middleware's HasScope gate must short-circuit before the handler runs.
        var fileName = $"scope-{Guid.NewGuid():N}.txt";
        var fullClient = await CreateAuthenticatedClientAsync();
        await PutAsync(fullClient, fileName, Encoding.UTF8.GetBytes("payload"));

        using var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword,
            scopes: "files.read");
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var (accessToken, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);
        var readOnlyClient = factory.CreateAuthenticatedClient(accessToken);

        using var del = new HttpRequestMessage(HttpMethod.Delete, $"/dav/{DriveName}/{fileName}");
        using var response = await readOnlyClient.SendAsync(del);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "authenticated-but-unscoped clients must hit 403 — credential is valid, permission is missing");
    }

    [Fact]
    public async Task MkCol_without_files_write_scope_returns_403_Forbidden()
    {
        using var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword,
            scopes: "files.read");
        var (accessToken, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);
        var readOnlyClient = factory.CreateAuthenticatedClient(accessToken);

        using var mkcol = new HttpRequestMessage(new HttpMethod("MKCOL"), $"/dav/{DriveName}/scope-mkcol-{Guid.NewGuid():N}/");
        using var response = await readOnlyClient.SendAsync(mkcol);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Copy_without_files_write_scope_returns_403_Forbidden()
    {
        // Symmetric to Delete_/MkCol_without_files_write_scope_returns_403_Forbidden — pins the
        // same security-checklist invariant on the COPY surface.
        var src = $"copy-scope-{Guid.NewGuid():N}.txt";
        var fullClient = await CreateAuthenticatedClientAsync();
        await PutAsync(fullClient, src, Encoding.UTF8.GetBytes("payload"));

        using var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword,
            scopes: "files.read");
        var (accessToken, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);
        var readOnlyClient = factory.CreateAuthenticatedClient(accessToken);

        using var copy = new HttpRequestMessage(new HttpMethod("COPY"), $"/dav/{DriveName}/{src}");
        copy.Headers.Add("Destination", $"/dav/{DriveName}/copy-scope-dst-{Guid.NewGuid():N}.txt");
        using var response = await readOnlyClient.SendAsync(copy);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Move_without_files_write_scope_returns_403_Forbidden()
    {
        // Symmetric scope-gate pin for MOVE.
        var src = $"move-scope-{Guid.NewGuid():N}.txt";
        var fullClient = await CreateAuthenticatedClientAsync();
        await PutAsync(fullClient, src, Encoding.UTF8.GetBytes("payload"));

        using var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword,
            scopes: "files.read");
        var (accessToken, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);
        var readOnlyClient = factory.CreateAuthenticatedClient(accessToken);

        using var move = new HttpRequestMessage(new HttpMethod("MOVE"), $"/dav/{DriveName}/{src}");
        move.Headers.Add("Destination", $"/dav/{DriveName}/move-scope-dst-{Guid.NewGuid():N}.txt");
        using var response = await readOnlyClient.SendAsync(move);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Move_publishes_FileMovedEvent_to_outbox()
    {
        // AC pin: "MOVE fires FileMovedEvent outbox event". The handler publishes via
        // IPublishEndpoint which stages an OutboxMessage row in the same transaction as the
        // path mutation; the row persists at least one polling window (default 5s) before
        // MassTransit's outbox dispatcher claims it. Querying immediately after the response
        // is well within that window.
        var client = await CreateAuthenticatedClientAsync();
        var src = $"event-src-{Guid.NewGuid():N}.txt";
        var dst = $"event-dst-{Guid.NewGuid():N}.txt";
        await PutAsync(client, src, Encoding.UTF8.GetBytes("payload"));

        using var move = new HttpRequestMessage(new HttpMethod("MOVE"), $"/dav/{DriveName}/{src}");
        move.Headers.Add("Destination", $"/dav/{DriveName}/{dst}");
        using var response = await client.SendAsync(move);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        await using var sp = BuildScopedDb();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var movedEventSeen = await db.Set<OutboxMessage>()
            .AsNoTracking()
            .AnyAsync(m => m.MessageType.Contains("FileMovedEvent"));
        movedEventSeen.Should().BeTrue(
            because: "MoveFileHandler publishes FileMovedEvent on the change tracker before " +
                     "SaveChangesAsync — an OutboxMessage row with that MessageType must exist " +
                     "after the MOVE response returns");
    }

    [Fact]
    public async Task Delete_does_not_remove_blob_from_storage_backend()
    {
        // AC pin: "DELETE does NOT physically remove files from storage (soft-delete only)".
        // The orphan-reaper is the authoritative storage sweep; DELETE just flips DeletedAt.
        // Reading the on-disk blob via File.Exists is the load-bearing assertion — without it
        // a regression that started physically deleting blobs would still show "FileItem
        // soft-deleted" in TC-003 but lose the actual bytes.
        var client = await CreateAuthenticatedClientAsync();
        var fileName = $"keep-bytes-{Guid.NewGuid():N}.txt";
        await PutAsync(client, fileName, Encoding.UTF8.GetBytes("must-stay-on-disk"));

        // Capture the StorageKey BEFORE the delete so we know which on-disk blob to check.
        string storageKey;
        await using (var sp = BuildScopedDb())
        {
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            storageKey = await db.Files.AsNoTracking()
                .Where(f => f.DriveId == _driveId && f.Path == fileName)
                .Select(f => f.StorageKey!)
                .SingleAsync();
        }

        using var del = new HttpRequestMessage(HttpMethod.Delete, $"/dav/{DriveName}/{fileName}");
        using var delResp = await client.SendAsync(del);
        delResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // LocalFileSystemProvider stores blobs at <rootPath>/<storageKey>. Path.Combine with the
        // forward-slashed storage key resolves to the right path on Linux + Windows.
        var blobPath = Path.Combine(_rootPath, storageKey.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(blobPath).Should().BeTrue(
            because: "soft-delete must NOT touch the storage backend; the orphan-reaper is the " +
                     "authoritative cleanup path. A regression that physically removes the blob " +
                     "would silently destroy the data the AC pins as recoverable.");
    }

    [Fact]
    public async Task MkCol_on_drive_root_returns_405_with_collection_Allow_header()
    {
        // RFC 4918 §9.3.1 — the drive root is the synthetic root collection (no FileItem to
        // create). 405 over 403 because the resource exists; the Allow header narrows to the
        // verbs legal on a collection-only target (no PUT, no DELETE, no MKCOL).
        var client = await CreateAuthenticatedClientAsync();

        using var mkcol = new HttpRequestMessage(new HttpMethod("MKCOL"), $"/dav/{DriveName}/");
        using var response = await client.SendAsync(mkcol);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        // HttpClient routes the Allow header to Content.Headers.Allow rather than
        // response.Headers — the split mirrors how System.Net.Http categorises entity-vs-message
        // headers. Either property must surface the value.
        response.Content.Headers.Allow.Should().NotBeEmpty(
            because: "RFC 4918 §10.4.2 — 405 responses MUST carry an Allow header naming the " +
                     "verbs the server WILL accept on this URL");
        response.Content.Headers.Allow.Should().NotContain("MKCOL",
            because: "the drive-root URL exists as a synthetic collection; MKCOL on it is " +
                     "permanently illegal so it must NOT be advertised in the Allow header");
    }

    [Fact]
    public async Task Move_with_Overwrite_F_returns_412_when_destination_exists()
    {
        // Parallel to TC004 (COPY Overwrite:F → 412) but on the MOVE surface. RFC 4918 §10.6
        // semantics apply equally to both verbs.
        var client = await CreateAuthenticatedClientAsync();
        var src = $"move-of-src-{Guid.NewGuid():N}.txt";
        var dst = $"move-of-dst-{Guid.NewGuid():N}.txt";
        await PutAsync(client, src, Encoding.UTF8.GetBytes("source"));
        await PutAsync(client, dst, Encoding.UTF8.GetBytes("blocker"));

        using var move = new HttpRequestMessage(new HttpMethod("MOVE"), $"/dav/{DriveName}/{src}");
        move.Headers.Add("Destination", $"/dav/{DriveName}/{dst}");
        move.Headers.Add("Overwrite", "F");
        using var response = await client.SendAsync(move);

        response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task Move_source_equals_destination_returns_409_Conflict_with_Strg_Reason()
    {
        // Edge case: source path == destination path. The middleware's pre-existence check on
        // the destination treats this as "destination exists" → 409 + OverwriteMoveDeferred
        // header (Overwrite:T default semantics deferred). RFC 4918 §9.9 does not mandate a
        // specific status for self-MOVE; pinning 409 here documents the actual behavior so a
        // future change that flips it (e.g. to 204 no-op) is caught.
        var client = await CreateAuthenticatedClientAsync();
        var path = $"self-move-{Guid.NewGuid():N}.txt";
        await PutAsync(client, path, Encoding.UTF8.GetBytes("payload"));

        using var move = new HttpRequestMessage(new HttpMethod("MOVE"), $"/dav/{DriveName}/{path}");
        move.Headers.Add("Destination", $"/dav/{DriveName}/{path}");
        using var response = await client.SendAsync(move);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Headers.Should().Contain(h => h.Key == "Strg-Reason",
            because: "the middleware sets Strg-Reason: OverwriteMoveDeferred so operators can " +
                     "tell the deferred-overwrite 409 apart from a race-window collision 409");
    }

    // ---- helpers ----

    private async Task<HttpResponseMessage> PutAsync(HttpClient client, string path, byte[] body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/dav/{DriveName}/{path}")
        {
            Content = new ByteArrayContent(body),
        };
        var response = await client.SendAsync(request);
        response.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.Created, HttpStatusCode.NoContent },
            because: "PUT must succeed for the seed phase to pin a meaningful precondition");
        return response;
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

    private async Task<Guid> EnsureDriveAsync(string driveName, string rootPath)
    {
        await using var sp = BuildScopedDb();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();

        var providerConfig = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["rootPath"] = rootPath,
        });

        var existing = await db.Drives.FirstOrDefaultAsync(d => d.Name == driveName);
        if (existing is not null)
        {
            existing.ProviderConfig = providerConfig;
            await db.SaveChangesAsync();
            return existing.Id;
        }

        var drive = new Drive
        {
            TenantId = factory.AdminTenantId,
            Name = driveName,
            ProviderType = "local",
            ProviderConfig = providerConfig,
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

    private ServiceProvider BuildScopedDb()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new FixtureTenantContext(factory.AdminTenantId));
        // StrgDbContext ctor depends on ICurrentUser; the throw-away container needs an explicit
        // registration since it doesn't inherit from the factory's service provider. Mirrors
        // StrgWebApplicationFactory.BootstrapSchemaAndSeedAsync.
        services.AddSingleton<ICurrentUser>(new FixtureCurrentUser(factory.AdminUserId));
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(factory.ConnectionString).UseOpenIddict());
        return services.BuildServiceProvider();
    }

    private sealed class FixtureTenantContext(Guid tenantId) : ITenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }

    private sealed class FixtureCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
    }
}
