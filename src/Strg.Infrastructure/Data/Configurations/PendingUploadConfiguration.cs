using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for <see cref="PendingUpload"/>. Indexes optimised for two readers: the
/// TUS pipeline's per-request <c>GetByUploadIdAsync</c> (unique on <see cref="PendingUpload.UploadId"/>)
/// and STRG-036's abandonment sweep ((TenantId, IsCompleted, ExpiresAt) composite — full index, not
/// partial; <c>WHERE ExpiresAt &gt; NOW()</c> is a Postgres <c>42P17</c> trap because <c>NOW()</c> is
/// <c>STABLE</c>, not <c>IMMUTABLE</c> — same lesson as <see cref="FileLockConfiguration"/>).
/// </summary>
public sealed class PendingUploadConfiguration : IEntityTypeConfiguration<PendingUpload>
{
    public void Configure(EntityTypeBuilder<PendingUpload> builder)
    {
        builder.HasKey(p => p.Id);

        builder.Property(p => p.UploadId).IsRequired();
        builder.Property(p => p.DriveId).IsRequired();
        builder.Property(p => p.UserId).IsRequired();

        // Path max 2048 mirrors Phase-5 design memory (path separator '/' max 2048).
        builder.Property(p => p.Path).IsRequired().HasMaxLength(2048);
        builder.Property(p => p.Filename).IsRequired().HasMaxLength(512);
        builder.Property(p => p.MimeType).IsRequired().HasMaxLength(255);
        builder.Property(p => p.DeclaredSize).IsRequired();
        builder.Property(p => p.ExpiresAt).IsRequired();

        // Same 2048 limit as Path — the temp key is structurally bounded by StrgUploadKeys.TempKey
        // (constant prefix + two N-formatted Guids = 51 chars), but accepting up to 2048 keeps the
        // column shape consistent across upload-related state (FileItem.Path, FileVersion.StorageKey,
        // PendingUpload.TempStorageKey).
        builder.Property(p => p.TempStorageKey).IsRequired().HasMaxLength(2048);

        builder.Property(p => p.UploadOffset).IsRequired().HasDefaultValue(0L);

        // WrappedDek is null between CREATE and the first chunk that drives encryption — see
        // PendingUpload class summary on the v0.1 encrypt-at-finalize protocol.
        builder.Property(p => p.WrappedDek);
        builder.Property(p => p.Algorithm).HasMaxLength(64);

        // SHA-256 hex = 64 chars.
        builder.Property(p => p.ContentHash).HasMaxLength(64);

        builder.Property(p => p.PlaintextSize);
        builder.Property(p => p.BlobSizeBytes);
        builder.Property(p => p.IsCompleted).IsRequired().HasDefaultValue(false);

        builder.HasIndex(p => p.UploadId).IsUnique();

        // Sweep query shape: WHERE TenantId = @t AND IsCompleted = false AND ExpiresAt < @now.
        // Composite chosen so the sweep planner can prune by tenant first, then filter by status,
        // then range-scan ExpiresAt. STRG-036 imports this index name verbatim.
        builder.HasIndex(p => new { p.TenantId, p.IsCompleted, p.ExpiresAt })
            .HasDatabaseName("IX_PendingUploads_TenantId_IsCompleted_ExpiresAt");
    }
}
