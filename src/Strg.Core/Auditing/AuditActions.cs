namespace Strg.Core.Auditing;

/// <summary>
/// Canonical string values for <see cref="Strg.Core.Domain.AuditEntry.Action"/>. Centralized so
/// filters, reports, and alerting queries can reference a single source of truth rather than
/// duplicating magic strings. Convention: lowercase dotted-segment pairs of
/// <c>resource.verb</c> or <c>resource.outcome</c>.
/// </summary>
public static class AuditActions
{
    /// <summary>
    /// Successful password-grant or refresh-token-grant token issuance. UserId + TenantId are
    /// populated from the authenticated user.
    /// </summary>
    public const string LoginSuccess = "login.success";

    /// <summary>
    /// Failed token-endpoint exchange — wrong password, unknown email, locked account, or a
    /// refresh token whose user is missing/deleted/locked. UserId and TenantId are
    /// <see cref="System.Guid.Empty"/> to avoid revealing whether a given email or subject exists.
    /// </summary>
    public const string LoginFailure = "login.failure";

    /// <summary>
    /// A user-scoped tag was assigned (or its value replaced) on a file. ResourceType is
    /// <c>"FileItem"</c>, ResourceId is the file id, UserId + TenantId populate the structural
    /// columns on <see cref="Strg.Core.Domain.AuditEntry"/>, and Details carries the normalised
    /// <c>(key, value_type)</c> pair.
    ///
    /// <para><b>Value deliberately excluded from Details.</b> Tag.Value is user-controlled up
    /// to 255 chars and may carry secrets (API keys, tokens, PII). The audit trail captures the
    /// fact of assignment, not its payload — pair with a per-value tagging policy when admin-
    /// visible value inspection is legitimately needed (v0.2 tracker).</para>
    /// </summary>
    public const string TagAssigned = "tag.assigned";

    /// <summary>
    /// A user-scoped tag was removed from a file. ResourceType is <c>"FileItem"</c>, ResourceId
    /// is the file id, UserId + TenantId populate the structural columns, and Details carries
    /// <c>key=...</c> for single-key removes or <c>bulk=true; count=N</c> for <c>RemoveAllAsync</c>.
    /// Idempotent removes (no row matched) do NOT emit — the audit log reflects state changes,
    /// not operator intent.
    /// </summary>
    public const string TagRemoved = "tag.removed";

    /// <summary>
    /// One or more <see cref="Strg.Core.Domain.FileVersion"/> rows were pruned (hard-deleted) from
    /// a file, either by the per-file VersionCount-exceeds-retention sweep or by explicit caller
    /// request via <see cref="Strg.Core.Services.IFileVersionStore.PruneVersionsAsync"/>.
    /// ResourceType is <c>"FileItem"</c>, ResourceId is the file id, UserId + TenantId populate
    /// the structural columns, and Details carries
    /// <c>retained_count=N; pruned_count=M; bytes_released=B</c>.
    ///
    /// <para>Per-version detail is intentionally omitted — the prune is a bulk retention op, and
    /// the surviving DB state already carries per-version metadata if an operator needs to
    /// reconstruct which specific VersionNumbers survived.</para>
    ///
    /// <para><b>Emitted only on full success.</b> If the per-version loop throws on iteration k,
    /// the exception propagates and no audit row is written — a partial row would mislead a
    /// reader into treating an interrupted prune as complete. The forensic trail for the
    /// committed <c>[0..k-1]</c> iterations lives in the DB itself (rows gone, quota released);
    /// the next successful retry emits its own audit row with the final <c>pruned_count</c>.</para>
    /// </summary>
    public const string FileVersionPruned = "file_version.pruned";

    /// <summary>
    /// A file was uploaded (new blob committed). ResourceType is <c>"FileItem"</c>, ResourceId is
    /// the file id, UserId + TenantId carry the uploader/tenant, and Details is a JSON object
    /// <c>{ driveId, size, mimeType }</c>. Emitted by <see cref="Strg.Infrastructure.Messaging.Consumers.AuditLogConsumer"/>
    /// from <c>FileUploadedEvent</c>; <see cref="Strg.Core.Domain.AuditEntry.EventId"/> is the
    /// outbox MessageId so redelivery is idempotent.
    /// </summary>
    public const string FileUploaded = "file.uploaded";

    /// <summary>
    /// A file was soft-deleted. ResourceType is <c>"FileItem"</c>, Details is
    /// <c>{ driveId }</c>. Emitted from <c>FileDeletedEvent</c>.
    /// </summary>
    public const string FileDeleted = "file.deleted";

    /// <summary>
    /// A file was moved (path changed). ResourceType is <c>"FileItem"</c>, Details is
    /// <c>{ driveId, oldPath, newPath }</c> — both paths recorded so the audit reader can
    /// reconstruct the pre/post layout without a separate table join. Emitted from
    /// <c>FileMovedEvent</c>.
    /// </summary>
    public const string FileMoved = "file.moved";
}
