using Strg.Core.Domain;

namespace Strg.Core.Events;

public sealed record FileUploadedEvent(
    Guid FileId, Guid DriveId, Guid UserId, Guid TenantId
) : IDomainEvent;
