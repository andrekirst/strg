using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Auditing;
using Xunit;

namespace Strg.Integration.Tests.Versioning;

/// <summary>
/// STRG-045 — REST file-version-restore endpoint integration tests. Class-scoped fixture
/// (<see cref="FileVersionRestoreEndpointFixture"/>) gives one PostgreSQL + RabbitMQ container
/// shared across all test methods. Each test seeds a fresh file so per-file state cannot
/// bleed between methods.
/// </summary>
public sealed class FileVersionRestoreEndpointTests(FileVersionRestoreEndpointFixture fx)
    : IClassFixture<FileVersionRestoreEndpointFixture>
{
    private static readonly byte[] V1Bytes = "version-one-content"u8.ToArray();
    private static readonly byte[] V2Bytes = "VERSION-TWO-totally-different-bytes-and-bigger"u8.ToArray();

    [Fact]
    public async Task TC001_RestoreV1_AfterV2_FileServesV1ContentAndVersionCountIsThree()
    {
        await fx.SeedPlaintextDriveAsync();
        var fileId = await fx.SeedFileWithInitialVersionAsync(V1Bytes, filename: "tc001.txt");
        await fx.AddVersionAsync(fileId, V2Bytes);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsync(
            $"/api/v1/drives/{fx.PlainDriveId}/files/{fileId}/versions/1/restore",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // FileItem now points at the restored content. CreateVersionAsync mutates Size /
        // ContentHash / VersionCount inside its tx, so a fresh reload reflects the post-restore
        // state irrespective of the request scope's cached entities.
        var reloaded = await fx.ReloadFileAsync(fileId);
        reloaded.VersionCount.Should().Be(3,
            "restore appends a NEW version (number = max + 1); v1 + v2 + v3 = three rows");
        reloaded.Size.Should().Be(V1Bytes.LongLength,
            "restore copies the source version's plaintext size into FileItem.Size");

        // Cross-check the restored bytes by reading version 3's blob through the provider —
        // pinning the "v3 serves v1's content" invariant directly. A regression where the
        // restore copy short-circuited (e.g., re-pointing FileItem.StorageKey at v1's key
        // without copying bytes) would still leave a v1-shaped current version, but the v3
        // FileVersion row would have v1's StorageKey instead of its own — caught here because
        // ReadVersionBytesAsync resolves the row's StorageKey, not FileItem's.
        var v3Bytes = await fx.ReadVersionBytesAsync(fileId, versionNumber: 3);
        v3Bytes.Should().Equal(V1Bytes);

        reloaded.ContentHash.Should().Be(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(V1Bytes)).ToLowerInvariant(),
            "restored file's ContentHash matches the source version's ContentHash");
    }

    [Fact]
    public async Task TC002_RestoreNonexistentVersion_Returns404()
    {
        await fx.SeedPlaintextDriveAsync();
        var fileId = await fx.SeedFileWithInitialVersionAsync(V1Bytes, filename: "tc002.txt");
        var preCount = await fx.CountVersionsAsync(fileId);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsync(
            $"/api/v1/drives/{fx.PlainDriveId}/files/{fileId}/versions/99/restore",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 404 must be a pure short-circuit — no version row, no blob, no quota delta. A
        // regression that wrote the new blob THEN checked GetVersionAsync would leak orphan
        // bytes and pollute the "no records added on 404" invariant.
        var postCount = await fx.CountVersionsAsync(fileId);
        postCount.Should().Be(preCount,
            "404 path must NOT add a new version row — handler must short-circuit before CreateVersionAsync");
    }

    [Fact]
    public async Task TC003_FileUploadedEvent_Published_AfterRestore()
    {
        // FileUploadedEvent is published via the outbox; AuditLogConsumer writes a row with
        // Action=file.uploaded once the dispatcher drains. Polling envelope mirrors
        // FileDeleteTests.TC004 — bare query without retry would race the dispatcher and flake.
        await fx.SeedPlaintextDriveAsync();
        var fileId = await fx.SeedFileWithInitialVersionAsync(V1Bytes, filename: "tc003.txt");
        await fx.AddVersionAsync(fileId, V2Bytes);

        // Snapshot the audit-row count BEFORE restore so the assertion reads "the restore
        // produced exactly one new file.uploaded entry", not "any entry exists" (the seed
        // path doesn't fire FileUploadedEvent because it bypasses the upload pipeline, but
        // pinning a delta is still tighter than pinning existence).
        await using var preCtx = fx.NewDbContext();
        var preCount = await preCtx.AuditEntries
            .CountAsync(e => e.Action == AuditActions.FileUploaded && e.ResourceId == fileId);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsync(
            $"/api/v1/drives/{fx.PlainDriveId}/files/{fileId}/versions/1/restore",
            content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        Strg.Core.Domain.AuditEntry? entry = null;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var ctx = fx.NewDbContext();
            entry = await ctx.AuditEntries
                .Where(e => e.Action == AuditActions.FileUploaded && e.ResourceId == fileId)
                .OrderByDescending(e => e.PerformedAt)
                .FirstOrDefaultAsync();
            if (entry is not null)
            {
                break;
            }
            await Task.Delay(500);
        }

        entry.Should().NotBeNull("FileUploadedEvent must reach AuditLogConsumer via the outbox");
        entry!.UserId.Should().Be(fx.UserId);
        entry.TenantId.Should().Be(fx.TenantId);
        entry.ResourceType.Should().Be("FileItem");
        entry.Details.Should().Contain($"\"driveId\":\"{fx.PlainDriveId}\"");

        await using var postCtx = fx.NewDbContext();
        var postCount = await postCtx.AuditEntries
            .CountAsync(e => e.Action == AuditActions.FileUploaded && e.ResourceId == fileId);
        postCount.Should().Be(preCount + 1,
            "exactly one FileUploadedEvent should fire per restore — duplicates would indicate a "
            + "double-publish or the outbox interceptor staging twice");
    }

    [Fact]
    public async Task TC004_VersionHistoryPreserved_AllThreeVersionsAccessibleAfterRestore()
    {
        // TC-004 from spec: "Version history: 1, 2, 3(restored-from-1) — version 1 content
        // accessible via all three". The acceptance criterion is the no-delete invariant from
        // the code-review checklist: "Restore does not delete any version records". This test
        // pins the contract by reading every version's blob via its OWN StorageKey post-restore.
        await fx.SeedPlaintextDriveAsync();
        var fileId = await fx.SeedFileWithInitialVersionAsync(V1Bytes, filename: "tc004.txt");
        await fx.AddVersionAsync(fileId, V2Bytes);

        var preCount = await fx.CountVersionsAsync(fileId);
        preCount.Should().Be(2, "seed produced v1 + v2");

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsync(
            $"/api/v1/drives/{fx.PlainDriveId}/files/{fileId}/versions/1/restore",
            content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Code-review checklist: count strictly grows by 1 — no v1/v2 rows deleted.
        var postCount = await fx.CountVersionsAsync(fileId);
        postCount.Should().Be(preCount + 1,
            "restore must APPEND, not replace — v1 + v2 + v3-from-v1 = 3 rows");

        var v1Bytes = await fx.ReadVersionBytesAsync(fileId, versionNumber: 1);
        var v2Bytes = await fx.ReadVersionBytesAsync(fileId, versionNumber: 2);
        var v3Bytes = await fx.ReadVersionBytesAsync(fileId, versionNumber: 3);

        v1Bytes.Should().Equal(V1Bytes, "v1 retains original content — never overwritten");
        v2Bytes.Should().Equal(V2Bytes, "v2 retains its content — never overwritten by the restore");
        v3Bytes.Should().Equal(V1Bytes, "v3 (restored) carries v1's bytes");

        // Three distinct storage keys: a regression where the restore re-pointed v3 at v1's
        // existing storage key would pass the byte-equality assertions above but break this
        // distinct-keys invariant (and also defeat retention pruning, which deletes by key).
        await using var ctx = fx.NewDbContext();
        var versions = await ctx.FileVersions
            .Where(v => v.FileId == fileId)
            .OrderBy(v => v.VersionNumber)
            .Select(v => v.StorageKey)
            .ToListAsync();
        versions.Should().HaveCount(3);
        versions.Distinct().Should().HaveCount(3,
            "each version owns its own storage key — the restored v3 must NOT alias v1's key");
    }

    [Fact]
    public async Task TC005_RestoreAcrossWrongDrive_Returns404()
    {
        // Cross-drive route mismatch → 404 (not 403) — same enumeration-oracle stance as
        // FileDeleteEndpoints + FileDownloadResolver.
        await fx.SeedPlaintextDriveAsync();
        var fileId = await fx.SeedFileWithInitialVersionAsync(V1Bytes, filename: "tc005.txt");
        var preCount = await fx.CountVersionsAsync(fileId);

        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsync(
            $"/api/v1/drives/{Guid.NewGuid()}/files/{fileId}/versions/1/restore",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var postCount = await fx.CountVersionsAsync(fileId);
        postCount.Should().Be(preCount, "wrong-drive route must NOT mutate the file's version history");
    }

    [Fact]
    public async Task WithoutFilesWriteScope_Returns403()
    {
        // RequireAuthorization(AuthPolicies.FilesWrite) gates the route — a token with
        // files.read but no files.write is rejected with 403 before the handler runs. Pins
        // the "Requires files.write scope" AC.
        await fx.SeedPlaintextDriveAsync();
        var fileId = await fx.SeedFileWithInitialVersionAsync(V1Bytes, filename: "scope.txt");

        var token = await fx.AuthenticateWithScopesAsync("files.read files.share");
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsync(
            $"/api/v1/drives/{fx.PlainDriveId}/files/{fileId}/versions/1/restore",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        await fx.SeedPlaintextDriveAsync();
        var fileId = await fx.SeedFileWithInitialVersionAsync(V1Bytes, filename: "anon.txt");

        using var client = fx.CreateClient();
        var response = await client.PostAsync(
            $"/api/v1/drives/{fx.PlainDriveId}/files/{fileId}/versions/1/restore",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnknownFile_Returns404()
    {
        await fx.SeedPlaintextDriveAsync();
        var token = await fx.AuthenticateAsync();
        using var client = fx.CreateAuthenticatedClient(token);

        var response = await client.PostAsync(
            $"/api/v1/drives/{fx.PlainDriveId}/files/{Guid.NewGuid()}/versions/1/restore",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
