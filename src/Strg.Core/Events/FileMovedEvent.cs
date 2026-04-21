using Strg.Core.Domain;

namespace Strg.Core.Events;

public sealed record FileMovedEvent(
    Guid TenantId, Guid FileId, Guid DriveId, string OldPath, string NewPath, Guid UserId
) : IDomainEvent;
