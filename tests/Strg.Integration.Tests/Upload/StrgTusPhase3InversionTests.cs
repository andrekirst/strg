using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Constants;
using Strg.Core.Storage;
using Xunit;

namespace Strg.Integration.Tests.Upload;

/// <summary>
/// TC-010 — phase-3 inversion: <see cref="IStorageProvider.MoveAsync"/> fails AFTER the DB
/// transaction commits. The expected (acceptable) state per spec:
/// <list type="bullet">
///   <item><c>FileItem</c> + <c>FileVersion</c> + <c>FileKey</c> rows exist (DB committed).</item>
///   <item>Temp blob remains at <c>pending.TempStorageKey</c>; final blob does NOT exist.</item>
///   <item><c>PendingUpload.IsCompleted</c> = true (sweep skips inside the safety window).</item>
/// </list>
/// STRG-036's backstop sweep is the recovery path. The TUS client receives a 500 (the re-thrown
/// MoveAsync exception) so they know the upload failed semantically; the path is reserved by the
/// FileItem unique constraint until the sweep reaps it.
/// </summary>
public sealed class StrgTusPhase3InversionTests(StrgTusPhase3InversionTests.MoveFailingFixture fx)
    : IClassFixture<StrgTusPhase3InversionTests.MoveFailingFixture>
{
    private readonly MoveFailingFixture _fx = fx;

    [Fact]
    public async Task TC010_MoveAsync_failure_post_DB_commit_keeps_temp_blob_and_pending_row_marked_completed()
    {
        var token = await _fx.AuthenticateAsync();
        using var client = _fx.CreateAuthenticatedClient(token);

        var plaintext = "phase-3-inversion test payload"u8.ToArray();
        var metadata = StrgTusUploadFixture.BuildMetadata("tc010/phase3.bin", "phase3.bin");

        using var createResponse = await _fx.CreateUploadAsync(client, plaintext.LongLength, metadata);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var uploadUrl = createResponse.Headers.Location!.ToString();

        // The PATCH that completes the upload triggers FinalizeAsync. With the hostile provider
        // the post-commit MoveAsync throws → tusdotnet emits 500. The DB has already committed.
        using var patchResponse = await _fx.PatchChunkAsync(client, uploadUrl, offset: 0, plaintext);
        patchResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        await using var ctx = _fx.NewDbContext();
        // DB-side: rows are committed. Phase-3 inversion does NOT roll back.
        var file = await ctx.Files.SingleAsync(f => f.Path == "tc010/phase3.bin");
        var version = await ctx.FileVersions.SingleAsync(v => v.FileId == file.Id);
        var key = await ctx.FileKeys.SingleAsync(k => k.FileVersionId == version.Id);

        file.Should().NotBeNull();
        version.StorageKey.Should().Be(StrgUploadKeys.FinalKey(_fx.DriveId, file.Id, 1));
        key.Algorithm.Should().NotBeNullOrEmpty();

        // Pending row stays with IsCompleted=true so STRG-036's sweep can recognise the inversion
        // state (vs. an in-flight upload).
        var uploadId = ParseUploadId(uploadUrl);
        var pending = await ctx.PendingUploads.SingleAsync(p => p.UploadId == uploadId);
        pending.IsCompleted.Should().BeTrue();

        // Storage-side: temp blob remains (the encrypting writer's output is at pending.TempStorageKey),
        // final blob does NOT exist (MoveAsync threw).
        File.Exists(Path.Combine(_fx.TempStorageRoot, pending.TempStorageKey))
            .Should().BeTrue("the encrypted blob is still at the temp key — STRG-036 sweep is the recovery path");
        File.Exists(Path.Combine(_fx.TempStorageRoot, version.StorageKey))
            .Should().BeFalse("MoveAsync threw before the bytes reached the final key");

        // Quota WAS charged: CommitAsync ran inside the DB tx and the tx committed. The user is
        // honestly debited even though the bytes aren't reachable yet — STRG-036's sweep is the
        // path that either re-runs MoveAsync or releases the quota when it gives up.
        (await _fx.ReadUsedBytesAsync()).Should().Be(plaintext.LongLength);
    }

    private static Guid ParseUploadId(string uploadUrl) =>
        Guid.ParseExact(uploadUrl[(uploadUrl.LastIndexOf('/') + 1)..], "N");

    /// <summary>
    /// Subclass of <see cref="StrgTusUploadFixture"/> that swaps <see cref="IStorageProviderRegistry"/>
    /// for one that wraps every resolved provider in a hostile decorator throwing
    /// <see cref="IOException"/> on <see cref="IStorageProvider.MoveAsync"/>.
    /// </summary>
    public sealed class MoveFailingFixture : StrgTusUploadFixture
    {
        protected override void ConfigureServicesOverride(IServiceCollection services)
        {
            var existing = services.SingleOrDefault(d => d.ServiceType == typeof(IStorageProviderRegistry));
            if (existing is not null)
            {
                services.Remove(existing);
            }
            services.AddSingleton<IStorageProviderRegistry>(_ =>
            {
                // Build a fresh registry with the same factory shape as production — with a
                // MoveFailing decorator wrapping the resolved provider.
                var registry = new Strg.Infrastructure.Storage.StorageProviderRegistry();
                registry.Register("local", config =>
                {
                    var rootPath = config.GetValue<string>("rootPath")
                        ?? throw new InvalidOperationException("'local' requires rootPath");
                    return new MoveFailingProvider(new Strg.Infrastructure.Storage.LocalFileSystemProvider(rootPath));
                });
                return registry;
            });
        }
    }

    /// <summary>
    /// Decorator that throws on <see cref="IStorageProvider.MoveAsync"/> and delegates everything
    /// else to <paramref name="inner"/>. Used exclusively by <see cref="MoveFailingFixture"/>.
    /// </summary>
    private sealed class MoveFailingProvider(IStorageProvider inner) : IStorageProvider
    {
        public string ProviderType => inner.ProviderType;

        public Task MoveAsync(string source, string destination, CancellationToken cancellationToken = default)
            => throw new IOException(
                $"Simulated phase-3 inversion: MoveAsync({source} → {destination}) failed after DB commit");

        public Task<IStorageFile?> GetFileAsync(string path, CancellationToken cancellationToken = default)
            => inner.GetFileAsync(path, cancellationToken);
        public Task<IStorageDirectory?> GetDirectoryAsync(string path, CancellationToken cancellationToken = default)
            => inner.GetDirectoryAsync(path, cancellationToken);
        public Task<Stream> ReadAsync(string path, long offset = 0, CancellationToken cancellationToken = default)
            => inner.ReadAsync(path, offset, cancellationToken);
        public Task WriteAsync(string path, Stream content, CancellationToken cancellationToken = default)
            => inner.WriteAsync(path, content, cancellationToken);
        public Task AppendAsync(string path, Stream content, CancellationToken cancellationToken = default)
            => inner.AppendAsync(path, content, cancellationToken);
        public Task CopyAsync(string source, string destination, CancellationToken cancellationToken = default)
            => inner.CopyAsync(source, destination, cancellationToken);
        public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(path, cancellationToken);
        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
            => inner.ExistsAsync(path, cancellationToken);
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
            => inner.CreateDirectoryAsync(path, cancellationToken);
        public IAsyncEnumerable<IStorageItem> ListAsync(string path, CancellationToken cancellationToken = default)
            => inner.ListAsync(path, cancellationToken);
    }
}
