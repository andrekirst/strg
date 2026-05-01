using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data.Configurations;

public sealed class ThumbnailEntryConfiguration : IEntityTypeConfiguration<ThumbnailEntry>
{
    public void Configure(EntityTypeBuilder<ThumbnailEntry> builder)
    {
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Variant).IsRequired().HasMaxLength(16);
        builder.Property(t => t.Format).IsRequired().HasMaxLength(16);
        builder.Property(t => t.StorageKey).IsRequired().HasMaxLength(512);
        builder.Property(t => t.GeneratorVersion).IsRequired().HasMaxLength(32);
        builder.Property(t => t.ErrorReason).HasMaxLength(256);

        // string conversion → postgres-readable enum surface; preserves easy migration
        // semantics (rename a member without a numeric remap) and lines up with the
        // equivalent decision on FileItem.MimeType (free-text). The trade-off is a few
        // bytes per row; the upside is grep-ability in pg_dump output during incident
        // triage.
        builder.Property(t => t.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(16);

        // Foreign key to FileVersion. Cascade so pruning a FileVersion row removes the
        // thumbnail rows in the same atomic step (STRG-329 — STRG-332 explicitly relies
        // on this to keep DB cleanup in the per-version transaction; the consumer is
        // only responsible for blob cleanup before the row removal).
        builder.HasOne<FileVersion>()
            .WithMany()
            .HasForeignKey(t => t.FileVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Idempotency key — the unique index the generation consumer's 23505 catch
        // discriminates against. Pinned name so a future EF rename surfaces as a
        // compile break in ThumbnailEntryConstraintNames.
        builder.HasIndex(t => new { t.FileVersionId, t.Variant, t.Format })
            .IsUnique()
            .HasDatabaseName(ThumbnailEntryConstraintNames.UniqueIndex);

        // Backfill enumeration query — `WHERE Status NOT IN (Ready, Unsupported)` is
        // the candidate-set predicate for `regenerateThumbnails`. Without this index a
        // backfill over a million-file drive would seq-scan ThumbnailEntries.
        builder.HasIndex(t => t.Status);

        // FileId index for the cleanup consumer — `WHERE FileId = @id` runs once per
        // FileDeletedEvent and the table can grow large.
        builder.HasIndex(t => t.FileId);
    }
}
