using Strg.Core.Domain;

namespace Strg.Core.Events;

public sealed record FileDeletedEvent(
    Guid FileId, Guid DriveId, Guid UserId, Guid TenantId
) : IDomainEvent;
