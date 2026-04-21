using FluentAssertions;
using Strg.Core.Domain;
using Xunit;

namespace Strg.Core.Tests.Domain;

public sealed class FileVersionTests
{
    [Fact]
    public void FileVersion_Defaults_CreatedAtIsRecent()
    {
        var before = DateTimeOffset.UtcNow;

        var version = new FileVersion
        {
            FileId = Guid.NewGuid(),
            VersionNumber = 1,
            Size = 1024,
            ContentHash = "abc",
            StorageKey = "drives/x/blobs/y",
            CreatedBy = Guid.NewGuid(),
        };

        version.CreatedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public void FileVersion_AllRequiredFieldsRoundTrip()
    {
        var fileId = Guid.NewGuid();
        var createdBy = Guid.NewGuid();

        var version = new FileVersion
        {
            FileId = fileId,
            VersionNumber = 7,
            Size = 9001,
            ContentHash = "deadbeef",
            StorageKey = "blob/storage/key",
            CreatedBy = createdBy,
        };

        version.FileId.Should().Be(fileId);
        version.VersionNumber.Should().Be(7);
        version.Size.Should().Be(9001);
        version.ContentHash.Should().Be("deadbeef");
        version.StorageKey.Should().Be("blob/storage/key");
        version.CreatedBy.Should().Be(createdBy);
    }
}
