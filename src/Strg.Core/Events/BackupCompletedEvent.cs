using Strg.Core.Domain;

namespace Strg.Core.Events;

public sealed record BackupCompletedEvent(
    Guid TenantId, Guid DriveId, long BytesWritten, TimeSpan Duration
) : IDomainEvent;
