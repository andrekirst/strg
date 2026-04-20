namespace Strg.Core.Events;

public enum FileEventType { Uploaded, Deleted, Moved, Copied, Renamed }

public sealed record FileEvent(
    FileEventType EventType,
    Guid FileId,
    Guid DriveId,
    Guid UserId,
    Guid TenantId,
    string? OldPath,
    string? NewPath,
    DateTimeOffset OccurredAt
);
