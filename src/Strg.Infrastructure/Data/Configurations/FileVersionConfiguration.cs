using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data.Configurations;

public sealed class FileVersionConfiguration : IEntityTypeConfiguration<FileVersion>
{
    public void Configure(EntityTypeBuilder<FileVersion> builder)
    {
        builder.HasKey(v => v.Id);
        builder.Property(v => v.ContentHash).IsRequired().HasMaxLength(64);
        builder.Property(v => v.StorageKey).IsRequired().HasMaxLength(1024);
        // Default 0 backfills pre-STRG-043 rows at migration time. Those rows predate envelope
        // metrics and are effectively unknown — operators querying storage-planning data should
        // treat 0 as "unreported" rather than "zero-byte blob". For new writes the store layer
        // always passes the real value (plaintext + envelope overhead for encrypted drives).
        builder.Property(v => v.BlobSizeBytes).HasDefaultValue(0L);
        builder.HasIndex(v => new { v.FileId, v.VersionNumber }).IsUnique();
    }
}
