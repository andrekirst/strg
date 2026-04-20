using Strg.Core.Domain;

namespace Strg.Core.Events;

public sealed record FileRenamedEvent(
    Guid FileId, Guid DriveId, Guid UserId, Guid TenantId,
    string OldName, string NewName
) : IDomainEvent;
