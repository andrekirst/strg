using Strg.Core.Domain;

namespace Strg.Core.Events;

public sealed record FileCopiedEvent(
    Guid FileId, Guid DriveId, Guid UserId, Guid TenantId,
    string NewPath
) : IDomainEvent;
