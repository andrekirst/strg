using Strg.Core.Domain;

namespace Strg.Core.Events;

/// <summary>
/// Backfill trigger published by the admin <c>regenerateThumbnails</c> mutation. Consumed by
/// <c>ThumbnailGenerationConsumer</c> via the same shared orchestration as
/// <c>FileUploadedEvent</c>.
///
/// <para><b>Dedicated event, not republished <c>FileUploadedEvent</c>.</b> Republishing the
/// upload event would double-write <c>AuditEntry</c> rows via <c>AuditLogConsumer</c>. The
/// dedicated event has no audit consumer; only the thumbnail generation consumer subscribes
/// to it.</para>
/// </summary>
public sealed record ThumbnailGenerationRequestedEvent(
    Guid TenantId,
    Guid FileId,
    Guid FileVersionId,
    Guid DriveId) : IDomainEvent;
