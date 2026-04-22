namespace Strg.Infrastructure.Data.Configurations;

internal static class AuditEntryConstraintNames
{
    // Authoritative name of the partial unique index on AuditEntry.EventId. Three
    // consumers need the same string: AuditEntryConfiguration (EF pin via
    // HasDatabaseName), AuditLogConsumer.IsEventIdUniqueViolation (equality-match
    // on Npgsql PostgresException.ConstraintName to discriminate at-least-once
    // redelivery from unrelated unique violations), and MigrationTests (schema
    // pin). Centralising the literal here turns a silent substring-match drift
    // into a compile break.
    public const string EventIdUniqueIndex = "IX_AuditEntries_EventId";
}
