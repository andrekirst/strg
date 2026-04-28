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

    [Fact]
    public void RebaseStorage_FlipsStorageKey_AndBlobSize_PreservesSize_AndContentHash()
    {
        // STRG-040 Phase 2 — cross-drive bytes-relocation primitive. Plaintext Size and
        // ContentHash are invariant; only the storage envelope (key + on-disk blob size) changes.
        var version = new FileVersion
        {
            FileId = Guid.NewGuid(),
            VersionNumber = 1,
            Size = 1024,
            BlobSizeBytes = 1056, // pre-rebase blob (with envelope) on encrypted source
            ContentHash = "abcdef",
            StorageKey = "drives/source/files/x/v1",
            CreatedBy = Guid.NewGuid(),
        };

        version.RebaseStorage("drives/target/files/x/v1", 1024); // plaintext target → blob size matches plaintext

        version.StorageKey.Should().Be("drives/target/files/x/v1");
        version.BlobSizeBytes.Should().Be(1024);
        version.Size.Should().Be(1024); // plaintext size invariant
        version.ContentHash.Should().Be("abcdef"); // hash invariant
    }

    [Fact]
    public void RebaseStorage_EmptyKey_Throws()
    {
        var version = new FileVersion
        {
            FileId = Guid.NewGuid(),
            VersionNumber = 1,
            Size = 1,
            ContentHash = "h",
            StorageKey = "key/initial",
            CreatedBy = Guid.NewGuid(),
        };

        var act = () => version.RebaseStorage("   ", 1);

        act.Should().Throw<ArgumentException>().WithParameterName("newStorageKey");
    }

    [Fact]
    public void RebaseStorage_NegativeBlobSize_Throws()
    {
        var version = new FileVersion
        {
            FileId = Guid.NewGuid(),
            VersionNumber = 1,
            Size = 1,
            ContentHash = "h",
            StorageKey = "key/initial",
            CreatedBy = Guid.NewGuid(),
        };

        var act = () => version.RebaseStorage("new/key", -1);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("newBlobSize");
    }
}
