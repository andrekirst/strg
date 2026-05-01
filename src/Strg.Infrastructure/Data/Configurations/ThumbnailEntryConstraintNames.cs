namespace Strg.Infrastructure.Data.Configurations;

internal static class ThumbnailEntryConstraintNames
{
    // Authoritative name of the unique index on (FileVersionId, Variant, Format).
    // Three consumers need the same string: ThumbnailEntryConfiguration (EF pin via
    // HasDatabaseName), ThumbnailGenerationConsumer.IsThumbnailUniqueViolation
    // (equality-match on Npgsql PostgresException.ConstraintName to discriminate
    // at-least-once redelivery from unrelated unique violations), and MigrationTests
    // (schema pin). Centralising the literal here turns a silent substring-match
    // drift into a compile break — same triangulation as AuditEntryConstraintNames.
    public const string UniqueIndex = "IX_ThumbnailEntries_FileVersionId_Variant_Format";
}
