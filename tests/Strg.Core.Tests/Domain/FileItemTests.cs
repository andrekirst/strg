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

    [Fact]
    public void MoveTo_MutatesDriveId_Path_AndName_Together()
    {
        // STRG-040 — MoveTo is the only call site that can flip DriveId after construction;
        // pin the lockstep mutation so a future refactor that splits the three writes is caught.
        var originalDriveId = Guid.NewGuid();
        var newDriveId = Guid.NewGuid();
        var file = new FileItem
        {
            DriveId = originalDriveId,
            Name = "old.txt",
            Path = "folder/old.txt",
            TenantId = Guid.NewGuid(),
            CreatedBy = Guid.NewGuid(),
        };

        file.MoveTo(newDriveId, "archive/2024/new.txt", "new.txt");

        file.DriveId.Should().Be(newDriveId);
        file.Path.Should().Be("archive/2024/new.txt");
        file.Name.Should().Be("new.txt");
    }

    [Fact]
    public void MoveTo_RejectsEmptyDriveId()
    {
        var file = new FileItem
        {
            DriveId = Guid.NewGuid(),
            Name = "old.txt",
            Path = "folder/old.txt",
            TenantId = Guid.NewGuid(),
            CreatedBy = Guid.NewGuid(),
        };

        var act = () => file.MoveTo(Guid.Empty, "new.txt", "new.txt");

        act.Should().Throw<ArgumentException>().WithParameterName("newDriveId");
    }

    [Fact]
    public void MoveTo_RejectsEmptyPath()
    {
        var file = new FileItem
        {
            DriveId = Guid.NewGuid(),
            Name = "old.txt",
            Path = "folder/old.txt",
            TenantId = Guid.NewGuid(),
            CreatedBy = Guid.NewGuid(),
        };

        var act = () => file.MoveTo(Guid.NewGuid(), "   ", "new.txt");

        act.Should().Throw<ArgumentException>().WithParameterName("newPath");
    }

    [Fact]
    public void MoveTo_RejectsEmptyName()
    {
        var file = new FileItem
        {
            DriveId = Guid.NewGuid(),
            Name = "old.txt",
            Path = "folder/old.txt",
            TenantId = Guid.NewGuid(),
            CreatedBy = Guid.NewGuid(),
        };

        var act = () => file.MoveTo(Guid.NewGuid(), "new.txt", "");

        act.Should().Throw<ArgumentException>().WithParameterName("newName");
    }

    [Fact]
    public void RebaseUnder_RewritesPath_PreservesName_FlipsDriveId()
    {
        // STRG-040 Phase 2 — descendant rewrite under a directory move. The leaf segment is
        // invariant (Name preserved); only the prefix path and the drive id move.
        var oldDriveId = Guid.NewGuid();
        var newDriveId = Guid.NewGuid();
        var file = new FileItem
        {
            DriveId = oldDriveId,
            Name = "file.txt",
            Path = "dir/sub/file.txt",
            TenantId = Guid.NewGuid(),
            CreatedBy = Guid.NewGuid(),
        };

        file.RebaseUnder("dir", "renamed", newDriveId);

        file.Path.Should().Be("renamed/sub/file.txt");
        file.Name.Should().Be("file.txt"); // leaf invariant
        file.DriveId.Should().Be(newDriveId);
    }

    [Fact]
    public void RebaseUnder_NonDescendantPath_Throws()
    {
        // Path="unrelated/x.txt" doesn't start with "dir/" — programming error in caller, surfaces
        // as InvalidOperationException (state error) not ArgumentException (input shape error).
        var file = new FileItem
        {
            DriveId = Guid.NewGuid(),
            Name = "x.txt",
            Path = "unrelated/x.txt",
            TenantId = Guid.NewGuid(),
            CreatedBy = Guid.NewGuid(),
        };

        var act = () => file.RebaseUnder("dir", "renamed", Guid.NewGuid());

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RebaseUnder_EmptyOldRoot_Throws()
    {
        var file = new FileItem
        {
            DriveId = Guid.NewGuid(),
            Name = "file.txt",
            Path = "dir/file.txt",
            TenantId = Guid.NewGuid(),
            CreatedBy = Guid.NewGuid(),
        };

        var act = () => file.RebaseUnder("   ", "renamed", Guid.NewGuid());

        act.Should().Throw<ArgumentException>().WithParameterName("oldRootPath");
    }

    [Fact]
    public void RebaseUnder_EmptyNewRoot_Throws()
    {
        var file = new FileItem
        {
            DriveId = Guid.NewGuid(),
            Name = "file.txt",
            Path = "dir/file.txt",
            TenantId = Guid.NewGuid(),
            CreatedBy = Guid.NewGuid(),
        };

        var act = () => file.RebaseUnder("dir", "", Guid.NewGuid());

        act.Should().Throw<ArgumentException>().WithParameterName("newRootPath");
    }

    [Fact]
    public void RebaseUnder_EmptyDriveId_Throws()
    {
        var file = new FileItem
        {
            DriveId = Guid.NewGuid(),
            Name = "file.txt",
            Path = "dir/file.txt",
            TenantId = Guid.NewGuid(),
            CreatedBy = Guid.NewGuid(),
        };

        var act = () => file.RebaseUnder("dir", "renamed", Guid.Empty);

        act.Should().Throw<ArgumentException>().WithParameterName("newDriveId");
    }
}
