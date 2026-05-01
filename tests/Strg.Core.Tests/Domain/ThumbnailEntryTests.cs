using FluentAssertions;
using Strg.Core.Domain;
using Xunit;

namespace Strg.Core.Tests.Domain;

public sealed class ThumbnailEntryTests
{
    [Fact]
    public void Defaults_AreSensible()
    {
        var entry = new ThumbnailEntry
        {
            FileVersionId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            Variant = "small",
            Format = "webp",
            GeneratorVersion = "magick-net-q8/v1",
        };

        entry.Status.Should().Be(ThumbnailStatus.Pending);
        entry.StorageKey.Should().BeEmpty();
        entry.SizeBytes.Should().Be(0);
        entry.Width.Should().Be(0);
        entry.Height.Should().Be(0);
        entry.GeneratedAt.Should().BeNull();
        entry.ErrorReason.Should().BeNull();
        entry.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void TenantedEntity_InheritsTenantId()
    {
        var tenantId = Guid.NewGuid();
        var entry = new ThumbnailEntry
        {
            TenantId = tenantId,
            FileVersionId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            Variant = "thumb",
            Format = "webp",
            GeneratorVersion = "magick-net-q8/v1",
        };
        entry.TenantId.Should().Be(tenantId);
    }
}
