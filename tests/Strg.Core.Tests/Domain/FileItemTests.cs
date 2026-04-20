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
        file.MimeType.Should().Be("application/octet-stream");
        file.VersionCount.Should().Be(1);
        file.IsDirectory.Should().BeFalse();
        file.ContentHash.Should().BeNull();
    }
}
