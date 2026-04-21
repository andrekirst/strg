using Strg.Core.Domain;

namespace Strg.Core.Events;

public sealed record FileUploadedEvent(
    Guid TenantId, Guid FileId, Guid DriveId, Guid UserId, long Size, string MimeType
) : IDomainEvent;
