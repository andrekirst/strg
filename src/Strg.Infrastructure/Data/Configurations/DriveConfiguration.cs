using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data.Configurations;

public sealed class DriveConfiguration : IEntityTypeConfiguration<Drive>
{
    public void Configure(EntityTypeBuilder<Drive> builder)
    {
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Name).IsRequired().HasMaxLength(64);
        builder.Property(d => d.ProviderType).IsRequired().HasMaxLength(50);
        // 8192-char cap is defense-in-depth against admin-side misconfiguration (or a compromised
        // admin) dropping a multi-megabyte JSON payload that every subsequent version operation
        // on the drive would have to re-parse. ProviderConfig is admin-set (trust boundary is
        // high), so this is not a primary validation surface — GraphQL/REST inputs should bound
        // payload size at the transport layer too, but the DB column is the last backstop.
        // 8192 is generous: the largest realistic payload is an S3 config (endpoint, bucket,
        // region, credentials reference, optional SSE params) which fits comfortably under 1 KiB.
        builder.Property(d => d.ProviderConfig).IsRequired().HasMaxLength(8192);
        builder.Property(d => d.VersioningPolicy).IsRequired().HasColumnType("text");
        builder.HasIndex(d => new { d.TenantId, d.Name }).IsUnique();
        builder.Ignore(d => d.IsDeleted);
    }
}
