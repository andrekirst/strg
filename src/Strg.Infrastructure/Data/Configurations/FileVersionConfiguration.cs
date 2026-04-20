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
        builder.HasIndex(v => new { v.FileId, v.VersionNumber }).IsUnique();
    }
}
