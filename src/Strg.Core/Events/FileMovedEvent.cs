using Strg.Core.Domain;

namespace Strg.Core.Events;

public sealed record FileMovedEvent(
    Guid FileId, Guid DriveId, Guid UserId, Guid TenantId,
    string OldPath, string NewPath
) : IDomainEvent;
