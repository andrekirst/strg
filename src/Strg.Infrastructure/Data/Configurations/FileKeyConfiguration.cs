using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data.Configurations;

public sealed class FileKeyConfiguration : IEntityTypeConfiguration<FileKey>
{
    public void Configure(EntityTypeBuilder<FileKey> builder)
    {
        builder.HasKey(k => k.Id);

        // Algorithm identifier is short by design ("AES-256-GCM" etc.). Clamping the column length
        // prevents future-you from silently persisting a 4KB garbage value that breaks lookup code.
        builder.Property(k => k.Algorithm).IsRequired().HasMaxLength(32);
        builder.Property(k => k.EncryptedDek).IsRequired();

        // One-to-one with FileVersion via a unique index on FileVersionId. The unique index is
        // load-bearing: overwriting a FileVersion without the unique constraint could leave two
        // FileKey rows on a crash-during-overwrite path, orphaning one DEK.
        builder.HasIndex(k => k.FileVersionId).IsUnique();

        // FK to FileVersion with Cascade — deleting a FileVersion purges its key. Purge policy is
        // handled upstream (soft-delete first, purge job reaps later), so cascade here matches the
        // "this FileVersion is truly gone" semantics at the DB layer.
        builder.HasOne<FileVersion>()
            .WithOne()
            .HasForeignKey<FileKey>(k => k.FileVersionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
