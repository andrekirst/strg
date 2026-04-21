using Strg.Core.Domain;

namespace Strg.Core.Events;

public sealed record FileDeletedEvent(
    Guid TenantId, Guid FileId, Guid DriveId, Guid UserId
) : IDomainEvent;
