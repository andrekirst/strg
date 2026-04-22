namespace Strg.Infrastructure.Data.Configurations;

internal static class NotificationConstraintNames
{
    // Authoritative name of the partial unique index on Notification.EventId — the
    // Notifications-side twin of AuditEntryConstraintNames.EventIdUniqueIndex. Shared
    // between NotificationConfiguration (HasDatabaseName pin), QuotaNotificationConsumer's
    // IsDuplicateEventId (equality-match discriminator for at-least-once redelivery),
    // and MigrationTests (schema pin). Substring-matching on "EventId" would silently
    // mis-classify a routine rename as "not the idempotency constraint" and retry every
    // duplicate event into the DLQ.
    public const string EventIdUniqueIndex = "IX_Notifications_EventId";
}
