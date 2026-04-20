using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data.Configurations;

public sealed class FileItemConfiguration : IEntityTypeConfiguration<FileItem>
{
    public void Configure(EntityTypeBuilder<FileItem> builder)
    {
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Name).IsRequired().HasMaxLength(255);
        builder.Property(f => f.Path).IsRequired().HasMaxLength(2048);
        builder.Property(f => f.MimeType).HasMaxLength(255);
        builder.Property(f => f.ContentHash).HasMaxLength(64);
        builder.HasIndex(f => new { f.DriveId, f.Path }).IsUnique();
        builder.Ignore(f => f.IsDeleted);
    }
}
