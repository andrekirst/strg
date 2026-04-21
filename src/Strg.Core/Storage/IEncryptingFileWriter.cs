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
/// <para><b>Out-of-scope (v0.2 trackers):</b> KEK rotation (needs a key-version return value),
/// chunked streaming encryption (the v0.1 implementation buffers in memory; single-shot AES-GCM
/// forces a file-size cap).</para>
/// </summary>
public interface IEncryptingFileWriter
{
    /// <summary>
    /// Generates a fresh DEK, AES-256-GCM-encrypts <paramref name="content"/>, writes the
    /// ciphertext envelope to the underlying provider at <paramref name="storageKey"/>, and
    /// returns the KEK-wrapped DEK. The caller persists the wrapped DEK in a
    /// <see cref="Strg.Core.Domain.FileKey"/> row within the same <c>SaveChangesAsync</c> as
    /// the owning <see cref="Strg.Core.Domain.FileVersion"/>.
    /// </summary>
    Task<EncryptedWriteResult> WriteAsync(string storageKey, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the ciphertext envelope from the underlying provider, unwraps
    /// <paramref name="wrappedDek"/> with the KEK, authenticates via GCM tag, and returns a
    /// plaintext stream positioned at <paramref name="offset"/>. Authentication covers the
    /// entire ciphertext — partial-stream reads would bypass tag verification, so the full
    /// envelope is always decrypted before <paramref name="offset"/> is applied.
    /// </summary>
    Task<Stream> ReadAsync(string storageKey, byte[] wrappedDek, long offset = 0, CancellationToken cancellationToken = default);
}

/// <summary>
/// Returned by <see cref="IEncryptingFileWriter.WriteAsync"/>. The caller persists
/// <see cref="WrappedDek"/> and <see cref="Algorithm"/> into the <c>file_keys</c> table and
/// records <see cref="Length"/> as the plaintext size on the <c>FileVersion</c> row.
/// </summary>
public sealed record EncryptedWriteResult(byte[] WrappedDek, string Algorithm, long Length);
