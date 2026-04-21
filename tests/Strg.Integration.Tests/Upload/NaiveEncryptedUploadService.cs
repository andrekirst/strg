using System.Security.Cryptography;
using Strg.Core.Domain;
using Strg.Core.Services;
using Strg.Core.Storage;
using Strg.Infrastructure.Storage.Encryption;

namespace Strg.Integration.Tests.Upload;

/// <summary>
/// Test-only naive orchestrator that composes <see cref="IEncryptingFileWriter"/> and
/// <see cref="IFileVersionStore"/> in the obvious blob-first-then-DB order. Not a production
/// surface — lives in the test project solely to pin the orphan-reaping contract on a commit
/// order that future STRG-032 / STRG-033 work cannot silently regress.
///
/// <para><b>Why naive.</b> Writes ciphertext to storage BEFORE
/// <see cref="IFileVersionStore.CreateVersionAsync"/> opens its transaction. When that transaction
/// fails (quota shortfall, unique-index race, DB connectivity), the DB state rolls back cleanly
/// but the blob stays on disk with no <see cref="FileVersion"/> row pointing at it — the orphan
/// ciphertext the <see cref="IEncryptingFileWriter"/> xmldoc says "is reaped by the purge job".
/// That reaper does not exist at HEAD; the integration tests in this folder codify the gap.</para>
///
/// <para><b>Deliberate omissions.</b> Does NOT persist the <see cref="FileKey"/>. The orphan-blob
/// question is orthogonal to FileKey persistence and bundling the two would conflate failure
/// modes. Production upload services must add FileKey persistence inside the same transaction
/// <see cref="IFileVersionStore.CreateVersionAsync"/> opens (pre-assigned <c>FileKey.FileVersionId</c>
/// against the staged <see cref="FileVersion.Id"/>).</para>
/// </summary>
internal sealed class NaiveEncryptedUploadService(
    IEncryptingFileWriter writer,
    IFileVersionStore versionStore,
    IStorageProvider provider)
{
    public string LastStorageKey { get; private set; } = string.Empty;

    public async Task<FileVersion> UploadAsync(
        FileItem file,
        byte[] plaintext,
        Guid uploadedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(plaintext);

        var storageKey = $"blobs/{file.Id:N}/{Guid.NewGuid():N}";
        LastStorageKey = storageKey;

        // Hash the plaintext BEFORE passing it to the writer — the writer consumes its stream
        // fully during encryption. Full-buffer hashing is acceptable for the small payloads these
        // tests exercise; a production implementation would thread a hashing wrapper through the
        // stream so plaintext never materialises twice.
        var contentHash = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();

        // Step 1 — blob-first. Ciphertext reaches storage BEFORE the DB transaction opens. This
        // is the ordering production STRG-032 / STRG-033 work has to either preserve behind a
        // reaper OR replace with a two-phase temp-then-promote protocol. The failure tests in
        // EncryptedUploadServiceTests observe the orphan this ordering produces.
        var writeResult = await writer
            .WriteAsync(storageKey, new MemoryStream(plaintext), AesGcmFileWriter.AlgorithmName, cancellationToken)
            .ConfigureAwait(false);

        // BlobSizeBytes is envelope-inclusive (header + per-chunk tags), NOT the plaintext length
        // the writer returns. FileVersion.Size and FileVersion.BlobSizeBytes diverge on encrypted
        // drives — the divergence is the whole point of devils-advocate STRG-026 #5.
        var blobFile = await provider.GetFileAsync(storageKey, cancellationToken).ConfigureAwait(false);
        var blobSizeBytes = blobFile?.Size ?? 0;

        // Step 2 — DB-second. A throw here orphans step 1's ciphertext.
        return await versionStore.CreateVersionAsync(
            file,
            storageKey,
            contentHash,
            size: writeResult.Length,
            blobSizeBytes: blobSizeBytes,
            createdBy: uploadedBy,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
