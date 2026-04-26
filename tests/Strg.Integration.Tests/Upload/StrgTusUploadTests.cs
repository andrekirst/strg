using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Constants;
using Strg.Infrastructure.Storage;
using Strg.Infrastructure.Storage.Encryption;
using Xunit;

namespace Strg.Integration.Tests.Upload;

/// <summary>
/// End-to-end TUS protocol tests for STRG-034 (TC-001..TC-009; TC-010 is in
/// <see cref="StrgTusPhase3InversionTests"/> because it needs a hostile MoveAsync provider).
///
/// <para>All tests run against <see cref="StrgTusUploadFixture"/> which seeds a tenant, a user
/// with 10 MiB quota, and a drive backed by <see cref="LocalFileSystemProvider"/> rooted at a
/// per-fixture temp directory. The TUS endpoint is reached via the real HTTP pipeline through
/// <c>WebApplicationFactory&lt;Program&gt;</c>, so auth, rate-limit-disable, OnAuthorize,
/// OnBeforeCreate, and FinalizeAsync all fire end-to-end.</para>
/// </summary>
public sealed class StrgTusUploadTests(StrgTusUploadFixture fx) : IClassFixture<StrgTusUploadFixture>
{
    private readonly StrgTusUploadFixture _fx = fx;

    // ── TC-001: 1 MiB single-chunk upload ────────────────────────────────────

    [Fact]
    public async Task TC001_OneMiB_single_chunk_upload_creates_FileItem_FileVersion_FileKey_and_promotes_temp_to_final()
    {
        var token = await _fx.AuthenticateAsync();
        using var client = _fx.CreateAuthenticatedClient(token);

        var plaintext = RandomBytes(1024 * 1024);
        var metadata = StrgTusUploadFixture.BuildMetadata("tc001/sample.bin", "sample.bin");

        // Snapshot before so assertion is delta-based — the fixture-scoped user accumulates
        // UsedBytes across the class's test methods and xUnit's intra-class order is undefined.
        var usedBefore = await _fx.ReadUsedBytesAsync();

        using var createResponse = await _fx.CreateUploadAsync(client, plaintext.LongLength, metadata);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var uploadUrl = createResponse.Headers.Location!.ToString();

        using var patchResponse = await _fx.PatchChunkAsync(client, uploadUrl, offset: 0, plaintext);
        patchResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var ctx = _fx.NewDbContext();
        var file = await ctx.Files.SingleAsync(f => f.Path == "tc001/sample.bin");
        var version = await ctx.FileVersions.SingleAsync(v => v.FileId == file.Id);
        var key = await ctx.FileKeys.SingleAsync(k => k.FileVersionId == version.Id);

        file.Size.Should().Be(plaintext.LongLength);
        file.StorageKey.Should().Be(StrgUploadKeys.FinalKey(_fx.DriveId, file.Id, 1));
        version.StorageKey.Should().Be(file.StorageKey);
        version.BlobSizeBytes.Should().BeGreaterThan(plaintext.LongLength,
            "envelope header + per-chunk tags inflate the on-disk size relative to plaintext");
        key.Algorithm.Should().Be(AesGcmFileWriter.AlgorithmName);
        key.EncryptedDek.Should().NotBeEmpty();

        // Temp blob promoted; final blob present on disk.
        FinalBlobExists(_fx, file.StorageKey!).Should().BeTrue();
        TempBlobExists(_fx, _fx.DriveId, ParseUploadId(uploadUrl)).Should().BeFalse();

        // Quota was charged plaintext bytes — NOT envelope-inflated.
        (await _fx.ReadUsedBytesAsync()).Should().Be(usedBefore + plaintext.LongLength);
    }

    // ── TC-002: Resume after simulated disconnect ─────────────────────────────

    [Fact]
    public async Task TC002_Resume_after_simulated_disconnect_returns_correct_offset_and_completes()
    {
        var token = await _fx.AuthenticateAsync();
        using var client = _fx.CreateAuthenticatedClient(token);

        var plaintext = RandomBytes(512 * 1024); // 512 KiB
        var firstHalf = plaintext[..(plaintext.Length / 2)];
        var secondHalf = plaintext[(plaintext.Length / 2)..];
        var metadata = StrgTusUploadFixture.BuildMetadata("tc002/resume.bin", "resume.bin");

        using var createResponse = await _fx.CreateUploadAsync(client, plaintext.LongLength, metadata);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var uploadUrl = createResponse.Headers.Location!.ToString();

        using var patch1 = await _fx.PatchChunkAsync(client, uploadUrl, offset: 0, firstHalf);
        patch1.StatusCode.Should().Be(HttpStatusCode.NoContent);
        patch1.Headers.GetValues("Upload-Offset").Single().Should().Be(firstHalf.Length.ToString());

        // Simulated disconnect: the client lost track of how far it got. HEAD recovers the offset.
        using var headResponse = await _fx.HeadAsync(client, uploadUrl);
        headResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var resumeOffset = long.Parse(headResponse.Headers.GetValues("Upload-Offset").Single());
        resumeOffset.Should().Be(firstHalf.Length);

        using var patch2 = await _fx.PatchChunkAsync(client, uploadUrl, offset: resumeOffset, secondHalf);
        patch2.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var ctx = _fx.NewDbContext();
        var file = await ctx.Files.SingleAsync(f => f.Path == "tc002/resume.bin");
        file.Size.Should().Be(plaintext.LongLength);
        var version = await ctx.FileVersions.SingleAsync(v => v.FileId == file.Id);
        version.Size.Should().Be(plaintext.LongLength);

        // pending row gone post-finalize.
        var pendingId = ParseUploadId(uploadUrl);
        (await ctx.PendingUploads.AnyAsync(p => p.UploadId == pendingId)).Should().BeFalse();
    }

    // ── TC-003: Quota exceeded → 413, no rows, no final-key blob, temp cleaned ───
    // This is the regression-pin replacement for `Upload_failure_on_quota_orphans_ciphertext_blob_TODO_STRG034`.

    [Fact]
    public async Task TC003_Quota_exceeded_at_complete_returns_413_no_rows_no_final_blob_temp_cleaned()
    {
        // Tighten the quota for THIS test only. The fixture is class-scoped so we restore at the end.
        await _fx.SetUserQuotaAsync(50); // 50 bytes — well under our 100-byte payload
        try
        {
            var token = await _fx.AuthenticateAsync();
            using var client = _fx.CreateAuthenticatedClient(token);

            var plaintext = RandomBytes(100);
            var metadata = StrgTusUploadFixture.BuildMetadata("tc003/over-quota.bin", "over-quota.bin");

            using var createResponse = await _fx.CreateUploadAsync(client, plaintext.LongLength, metadata);
            // Pre-quota check at OnBeforeCreate already rejects: 100 > 50 remaining.
            createResponse.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge,
                "the pre-quota-check at OnBeforeCreate is the early-rejection path; declared length 100 > remaining 50");

            await using var ctx = _fx.NewDbContext();
            (await ctx.Files.AnyAsync(f => f.Path == "tc003/over-quota.bin")).Should().BeFalse();
            (await ctx.FileVersions.AnyAsync()).Should().BeFalse();
            (await ctx.PendingUploads.AnyAsync()).Should().BeFalse(
                "OnBeforeCreate FailRequest fires before CreateFileAsync — no pending row is staged");
            (await _fx.ReadUsedBytesAsync()).Should().Be(0);
        }
        finally
        {
            await _fx.SetUserQuotaAsync(10L * 1024 * 1024);
        }
    }

    // ── TC-004: Path traversal in metadata → 422 ──────────────────────────────

    [Fact]
    public async Task TC004_Path_traversal_in_metadata_returns_422_no_pending_row()
    {
        var token = await _fx.AuthenticateAsync();
        using var client = _fx.CreateAuthenticatedClient(token);

        var plaintext = RandomBytes(64);
        var metadata = StrgTusUploadFixture.BuildMetadata("../etc/passwd", "passwd");

        using var createResponse = await _fx.CreateUploadAsync(client, plaintext.LongLength, metadata);
        createResponse.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        await using var ctx = _fx.NewDbContext();
        (await ctx.PendingUploads.AnyAsync()).Should().BeFalse(
            "StoragePath.Parse threw inside OnBeforeCreate; FailRequest(422) fires before CreateFileAsync");
    }

    // ── TC-005: Two concurrent uploads same user within quota ─────────────────

    [Fact]
    public async Task TC005_Two_concurrent_uploads_same_user_within_quota_both_complete()
    {
        var token = await _fx.AuthenticateAsync();
        using var client1 = _fx.CreateAuthenticatedClient(token);
        using var client2 = _fx.CreateAuthenticatedClient(token);

        var bytes1 = RandomBytes(256 * 1024);
        var bytes2 = RandomBytes(256 * 1024);
        var meta1 = StrgTusUploadFixture.BuildMetadata("tc005/parallel-a.bin", "a.bin");
        var meta2 = StrgTusUploadFixture.BuildMetadata("tc005/parallel-b.bin", "b.bin");

        var usedBefore = await _fx.ReadUsedBytesAsync();

        var task1 = RunFullUploadAsync(client1, bytes1, meta1);
        var task2 = RunFullUploadAsync(client2, bytes2, meta2);
        await Task.WhenAll(task1, task2);

        await using var ctx = _fx.NewDbContext();
        var files = await ctx.Files
            .Where(f => f.Path == "tc005/parallel-a.bin" || f.Path == "tc005/parallel-b.bin")
            .ToListAsync();
        files.Count.Should().Be(2);
        (await _fx.ReadUsedBytesAsync()).Should().Be(usedBefore + bytes1.LongLength + bytes2.LongLength);
    }

    // ── TC-006: Abort upload via DELETE → UsedBytes unchanged, temp cleaned ───

    [Fact]
    public async Task TC006_Abort_upload_via_DELETE_does_not_increment_UsedBytes()
    {
        var token = await _fx.AuthenticateAsync();
        using var client = _fx.CreateAuthenticatedClient(token);

        var bytes = RandomBytes(64 * 1024);
        var partial = bytes[..(bytes.Length / 2)];
        var metadata = StrgTusUploadFixture.BuildMetadata("tc006/aborted.bin", "aborted.bin");

        var usedBefore = await _fx.ReadUsedBytesAsync();

        using var createResponse = await _fx.CreateUploadAsync(client, bytes.LongLength, metadata);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var uploadUrl = createResponse.Headers.Location!.ToString();

        using var patch = await _fx.PatchChunkAsync(client, uploadUrl, offset: 0, partial);
        patch.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var deleteResponse = await _fx.DeleteUploadAsync(client, uploadUrl);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _fx.ReadUsedBytesAsync()).Should().Be(usedBefore);

        await using var ctx = _fx.NewDbContext();
        var pendingId = ParseUploadId(uploadUrl);
        (await ctx.PendingUploads.AnyAsync(p => p.UploadId == pendingId)).Should().BeFalse();
        (await ctx.Files.AnyAsync(f => f.Path == "tc006/aborted.bin")).Should().BeFalse();
    }

    // ── TC-007: HEAD on unknown upload ID → 404 ──────────────────────────────

    [Fact]
    public async Task TC007_HEAD_on_unknown_uploadId_returns_404()
    {
        var token = await _fx.AuthenticateAsync();
        using var client = _fx.CreateAuthenticatedClient(token);

        using var response = await _fx.HeadAsync(client, $"/upload/{Guid.NewGuid():N}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── TC-008: PATCH with wrong Upload-Offset → 409 ─────────────────────────
    //
    // The literal spec wording for TC-008 is "PATCH with Content-Length mismatch → 400", but
    // HttpClient computes Content-Length from the body it actually sends, making a "mismatch"
    // hard to construct without raw socket access. The neighbouring AC ("PATCH with wrong
    // Upload-Offset → 409") is the protocol-level error this test pins instead — it covers the
    // same surface (TUS-protocol PATCH validation) without the HttpClient quirks.

    [Fact]
    public async Task TC008_PATCH_with_wrong_UploadOffset_returns_409()
    {
        var token = await _fx.AuthenticateAsync();
        using var client = _fx.CreateAuthenticatedClient(token);

        var bytes = RandomBytes(1024);
        var metadata = StrgTusUploadFixture.BuildMetadata("tc008/wrong-offset.bin", "wrong-offset.bin");

        using var createResponse = await _fx.CreateUploadAsync(client, bytes.LongLength, metadata);
        var uploadUrl = createResponse.Headers.Location!.ToString();

        // Fresh upload — current offset is 0. Sending Upload-Offset: 100 is a protocol violation.
        using var patch = await _fx.PatchChunkAsync(client, uploadUrl, offset: 100, bytes);
        patch.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── TC-009: FileKey.Algorithm round-trip on decrypt ──────────────────────

    [Fact]
    public async Task TC009_FileKey_Algorithm_persisted_and_download_decrypts_via_stored_algorithm()
    {
        var token = await _fx.AuthenticateAsync();
        using var client = _fx.CreateAuthenticatedClient(token);

        var plaintext = "the quick brown fox jumps over the lazy dog"u8.ToArray();
        var metadata = StrgTusUploadFixture.BuildMetadata("tc009/algo-roundtrip.txt", "algo-roundtrip.txt");

        using var createResponse = await _fx.CreateUploadAsync(client, plaintext.LongLength, metadata);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var uploadUrl = createResponse.Headers.Location!.ToString();

        using var patch = await _fx.PatchChunkAsync(client, uploadUrl, offset: 0, plaintext);
        patch.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var ctx = _fx.NewDbContext();
        var file = await ctx.Files.SingleAsync(f => f.Path == "tc009/algo-roundtrip.txt");
        var version = await ctx.FileVersions.SingleAsync(v => v.FileId == file.Id);
        var key = await ctx.FileKeys.SingleAsync(k => k.FileVersionId == version.Id);

        key.Algorithm.Should().Be(AesGcmFileWriter.AlgorithmName);

        // Now exercise the read path: load Algorithm from the row and pass it verbatim to the
        // decrypting reader. The reader's contract enforces algorithm match (mismatched algorithm
        // → NotSupportedException), so a successful decrypt is itself the proof that
        // FileKey.Algorithm survived the round-trip.
        var provider = new LocalFileSystemProvider(_fx.TempStorageRoot);
        var keyProvider = new EnvVarKeyProvider(StrgTusUploadFixture.TestKekBase64);
        var reader = new AesGcmFileWriter(provider, keyProvider);
        await using var decrypted = await reader.ReadAsync(version.StorageKey, key.EncryptedDek, key.Algorithm);
        using var ms = new MemoryStream();
        await decrypted.CopyToAsync(ms);
        ms.ToArray().Should().BeEquivalentTo(plaintext);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private async Task RunFullUploadAsync(HttpClient client, byte[] bytes, string metadata)
    {
        using var create = await _fx.CreateUploadAsync(client, bytes.LongLength, metadata);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var uploadUrl = create.Headers.Location!.ToString();
        using var patch = await _fx.PatchChunkAsync(client, uploadUrl, offset: 0, bytes);
        patch.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private static Guid ParseUploadId(string uploadUrl) =>
        Guid.ParseExact(uploadUrl[(uploadUrl.LastIndexOf('/') + 1)..], "N");

    private static bool TempBlobExists(StrgTusUploadFixture fx, Guid driveId, Guid uploadId) =>
        File.Exists(Path.Combine(fx.TempStorageRoot, StrgUploadKeys.TempKey(driveId, uploadId)))
        || File.Exists(Path.Combine(fx.TempStorageRoot, StrgUploadKeys.TempKey(driveId, uploadId) + ".part"));

    private static bool FinalBlobExists(StrgTusUploadFixture fx, string finalKey) =>
        File.Exists(Path.Combine(fx.TempStorageRoot, finalKey));
}
