namespace Strg.Core.Domain;

/// <summary>
/// Server-side state for an in-flight TUS upload (STRG-034). One row exists from <c>POST /upload</c>
/// (CREATE) until either successful finalize or abandonment-sweep (STRG-036).
///
/// <para><b>Why a row, not just tusdotnet's file storage?</b> The two-phase finalize MUST stage
/// <see cref="WrappedDek"/> + <see cref="Algorithm"/> across many <c>PATCH</c> chunks; tusdotnet's
/// metadata header is opaque-string-only and an in-memory cache loses staged crypto material on
/// app restart. STRG-036 also needs a tenant-aware enumeration of orphans by <see cref="ExpiresAt"/>
/// — only a DB row supports a sub-second sweep query.</para>
///
/// <para><b>v0.1 encryption-at-finalize.</b> Per the writer's whole-stream contract
/// (<see cref="Storage.IEncryptingFileWriter"/> wraps <c>AesGcmFileWriter</c> which cannot
/// resume across PATCH boundaries), chunks are appended raw to <see cref="TempStorageKey"/> + a
/// <c>.part</c> sidecar, and the encrypting writer runs ONCE at the moment the upload reaches
/// <see cref="DeclaredSize"/>. <see cref="WrappedDek"/> + <see cref="Algorithm"/> are populated at
/// that moment and persisted with the row before the finalize transaction opens.</para>
/// </summary>
public sealed class PendingUpload : TenantedEntity
{
    public required Guid UploadId { get; init; }
    public required Guid DriveId { get; init; }
    public required Guid UserId { get; init; }
    public required string Path { get; init; }
    public required string Filename { get; init; }
    public required string MimeType { get; init; }
    public required long DeclaredSize { get; init; }

    /// <summary>
    /// Wall-clock deadline. STRG-036's sweep treats rows past this as abandoned and reaps both the
    /// row and any temp blob at <see cref="TempStorageKey"/>. Mutable so the abandonment window
    /// can be extended on long-running uploads (not exercised in v0.1).
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// The temp-namespaced storage key. Set at CREATE via
    /// <see cref="Constants.StrgUploadKeys.TempKey"/>; never the final key.
    /// </summary>
    public required string TempStorageKey { get; init; }

    /// <summary>
    /// Cumulative byte count of received chunks (raw plaintext). Tracks TUS protocol's
    /// <c>Upload-Offset</c> for resume support.
    /// </summary>
    public long UploadOffset { get; set; }

    /// <summary>
    /// KEK-wrapped DEK from <see cref="Storage.EncryptedWriteResult.WrappedDek"/>. Null until the
    /// final chunk drives the encrypting-writer pass — see class summary.
    /// </summary>
    public byte[]? WrappedDek { get; set; }

    /// <summary>
    /// Algorithm name from <see cref="Storage.EncryptedWriteResult.Algorithm"/>. Persisted to
    /// <see cref="FileKey.Algorithm"/> at finalize. Null until encryption has run.
    /// </summary>
    public string? Algorithm { get; set; }

    /// <summary>
    /// SHA-256 hex of the assembled plaintext, computed in-stream during the encrypting-writer
    /// pass. Persisted to <see cref="FileVersion.ContentHash"/> + <see cref="FileItem.ContentHash"/>
    /// at finalize.
    /// </summary>
    public string? ContentHash { get; set; }

    /// <summary>
    /// Plaintext size after the encrypting-writer pass (equal to <see cref="DeclaredSize"/> on the
    /// happy path; populated for sanity-check assertions).
    /// </summary>
    public long? PlaintextSize { get; set; }

    /// <summary>
    /// On-disk envelope size including AES-GCM header + per-chunk tags. Persisted to
    /// <see cref="FileVersion.BlobSizeBytes"/> at finalize. Not charged to user quota.
    /// </summary>
    public long? BlobSizeBytes { get; set; }

    /// <summary>
    /// Flips to <c>true</c> after the finalize DB transaction commits but BEFORE
    /// <see cref="Storage.IStorageProvider.MoveAsync"/> runs. STRG-036's sweep skips
    /// <c>IsCompleted = true</c> rows within a configurable safety window so an in-flight
    /// MoveAsync does not race the reaper.
    /// </summary>
    public bool IsCompleted { get; set; }
}
