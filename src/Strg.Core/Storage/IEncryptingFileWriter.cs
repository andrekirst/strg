namespace Strg.Core.Storage;

/// <summary>
/// Explicit collaborator — NOT an <see cref="IStorageProvider"/> decorator — that encrypts file
/// content with a freshly-generated Data Encryption Key (DEK), writes the ciphertext to an
/// underlying <see cref="IStorageProvider"/>, and returns the KEK-wrapped DEK for the caller
/// (the upload service) to persist alongside the <see cref="Strg.Core.Domain.FileVersion"/> row.
///
/// <para><b>Why not a decorator?</b> A transparent <see cref="IStorageProvider"/> decorator would
/// have to own two side effects (storage write + <see cref="Strg.Core.Domain.FileKey"/> DB write)
/// and commit them independently of the caller's transaction. That breaks transactional atomicity:
/// on DB failure after storage success, the ciphertext is unrecoverable because its DEK never
/// reached durable storage. With an explicit writer, the caller collects the wrapped DEK first,
/// stages the FileKey + FileVersion rows, and commits them together — DB failure rolls both back
/// and the orphan ciphertext is reaped by the purge job (STRG-036).</para>
///
/// <para><b>Two-phase upload contract (STRG-034).</b> The production caller — the TUS upload
/// endpoint implemented in STRG-034 — MUST invoke this writer into a temp-namespaced storage key
/// (e.g., <c>uploads/temp/{driveId}/{ulid}</c>), then on successful DB commit (FileVersion row +
/// FileKey row + <see cref="Strg.Core.Services.IQuotaService.CommitAsync"/>) promote the temp key
/// to the final key via <see cref="IStorageProvider.MoveAsync"/>. On DB-tx failure, the temp blob
/// is cleaned up by <see cref="IStorageProvider.DeleteAsync"/> (best-effort, idempotent per the
/// provider contract). This protocol closes the orphan-ciphertext gap captured in STRG-026 #2: the
/// temp blob never reaches the final key space on failure, so the purge job referenced above is
/// only a backstop for phase-4-cleanup flakes, not the primary recovery path. The test suite's
/// <c>NaiveEncryptedUploadService</c> is deliberately NOT two-phase — it is the regression-pinning
/// device for the gap, not production.</para>
///
/// <para><b>Out-of-scope (v0.2 trackers):</b> KEK rotation (needs a key-version return value),
/// chunked streaming encryption (the v0.1 implementation buffers in memory; single-shot AES-GCM
/// forces a file-size cap).</para>
/// </summary>
public interface IEncryptingFileWriter
{
    /// <summary>
    /// Generates a fresh DEK, encrypts <paramref name="content"/> with the cipher named by
    /// <paramref name="algorithm"/>, writes the ciphertext envelope to the underlying provider at
    /// <paramref name="storageKey"/>, and returns the KEK-wrapped DEK alongside the algorithm name
    /// that actually ran (which must match <paramref name="algorithm"/> — implementations reject
    /// algorithms they do not support rather than silently falling back). The caller persists the
    /// wrapped DEK in a <see cref="Strg.Core.Domain.FileKey"/> row within the same
    /// <c>SaveChangesAsync</c> as the owning <see cref="Strg.Core.Domain.FileVersion"/>.
    ///
    /// <para>The <paramref name="algorithm"/> parameter is the caller's explicit election — no
    /// default, no implicit pick-up-whatever-is-bound. v0.2 will introduce alternate ciphers
    /// (e.g., ChaCha20-Poly1305) and KEK-rotated variants, and the dispatcher will route
    /// on this string. Making it required NOW means no caller has ever baked in an implicit
    /// algorithm choice that would have to be unwound at lockdown.</para>
    /// </summary>
    Task<EncryptedWriteResult> WriteAsync(string storageKey, Stream content, string algorithm, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the ciphertext envelope from the underlying provider, unwraps
    /// <paramref name="wrappedDek"/> with the KEK using the cipher named by
    /// <paramref name="algorithm"/>, authenticates via the envelope's integrity tag(s), and
    /// returns a plaintext stream positioned at <paramref name="offset"/>. Authentication covers
    /// the entire ciphertext — partial-stream reads would bypass tag verification, so the full
    /// envelope is always decrypted before <paramref name="offset"/> is applied.
    ///
    /// <para>The <paramref name="algorithm"/> parameter is the v0.2 dispatch hook: callers pass
    /// the value stored on the envelope's <see cref="Strg.Core.Domain.FileKey"/> row, and the
    /// dispatcher routes to the implementation that owns that cipher. At v0.1 only one
    /// implementation exists and mismatched values are rejected with
    /// <see cref="NotSupportedException"/> — preferable to silently decrypting with the wrong
    /// cipher and returning tag-mismatch errors that mask the actual misconfiguration.</para>
    /// </summary>
    Task<Stream> ReadAsync(string storageKey, byte[] wrappedDek, string algorithm, long offset = 0, CancellationToken cancellationToken = default);
}

/// <summary>
/// Returned by <see cref="IEncryptingFileWriter.WriteAsync"/>. The caller persists
/// <see cref="WrappedDek"/> and <see cref="Algorithm"/> into the <c>file_keys</c> table and
/// records <see cref="Length"/> as the plaintext size on the <c>FileVersion</c> row.
/// </summary>
public sealed record EncryptedWriteResult(byte[] WrappedDek, string Algorithm, long Length);
