using System.Net.Mime;
using FluentAssertions;
using Strg.Core.Domain;
using Xunit;

namespace Strg.Core.Tests.Domain;

public sealed class FileItemTests
{
    [Fact]
    public void FileItem_HasCorrectDefaults()
    {
        var file = new FileItem
        {
            DriveId = Guid.NewGuid(),
            Name = "report.pdf",
            Path = "docs/report.pdf",
            TenantId = Guid.NewGuid(),
            CreatedBy = Guid.NewGuid()
        };
        file.MimeType.Should().Be(MediaTypeNames.Application.Octet);
        file.VersionCount.Should().Be(1);
        file.IsDirectory.Should().BeFalse();
        file.ContentHash.Should().BeNull();
    }

    [Fact]
    public void Directory_HasNullContentHash()
    {
        var folder = new FileItem
        {
            DriveId = Guid.NewGuid(),
            Name = "docs",
            Path = "docs",
            IsDirectory = true,
            TenantId = Guid.NewGuid(),
            CreatedBy = Guid.NewGuid()
        };

        folder.ContentHash.Should().BeNull();
        folder.IsDirectory.Should().BeTrue();
        folder.IsFolder.Should().BeTrue();
    }

    [Fact]
    public void IsDeleted_DerivedFromDeletedAt()
    {
        var file = new FileItem
        {
            DriveId = Guid.NewGuid(),
            Name = "report.pdf",
            Path = "docs/report.pdf",
            TenantId = Guid.NewGuid(),
            CreatedBy = Guid.NewGuid()
        };

        file.IsDeleted.Should().BeFalse();

        file.DeletedAt = DateTimeOffset.UtcNow;

        file.IsDeleted.Should().BeTrue();
    }
}
