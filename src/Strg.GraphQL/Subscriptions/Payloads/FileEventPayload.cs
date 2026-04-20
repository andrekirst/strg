using Strg.Core.Events;

namespace Strg.GraphQL.Subscriptions.Payloads;

// FileId stored by ID; the 'file' field is resolved lazily via DataLoader
public sealed record FileEventPayload(
    FileEventType EventType,
    Guid FileId,
    Guid DriveId,
    DateTimeOffset OccurredAt
);
