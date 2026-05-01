using FluentAssertions;
using Strg.Core.Services;
using Xunit;

namespace Strg.Core.Tests.Services;

public sealed class ThumbnailStorageKeyBuilderTests
{
    private static readonly Guid DriveId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid VersionId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Build_Canonical_Format()
    {
        var key = ThumbnailStorageKeyBuilder.Build(DriveId, VersionId, ThumbnailVariants.Small, ThumbnailFormats.WebP);
        key.Should().Be(
            "thumbnails/11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222/small.webp");
    }

    [Theory]
    [InlineData("thumb")]
    [InlineData("small")]
    [InlineData("medium")]
    public void Build_AllVariants_Succeed(string variant)
    {
        var act = () => ThumbnailStorageKeyBuilder.Build(DriveId, VersionId, variant, ThumbnailFormats.WebP);
        act.Should().NotThrow();
    }

    [Fact]
    public void Build_EmptyDriveId_Throws()
    {
        var act = () => ThumbnailStorageKeyBuilder.Build(Guid.Empty, VersionId, ThumbnailVariants.Thumb, ThumbnailFormats.WebP);
        act.Should().Throw<ArgumentException>().WithMessage("*Drive id*");
    }

    [Fact]
    public void Build_EmptyVersionId_Throws()
    {
        var act = () => ThumbnailStorageKeyBuilder.Build(DriveId, Guid.Empty, ThumbnailVariants.Thumb, ThumbnailFormats.WebP);
        act.Should().Throw<ArgumentException>().WithMessage("*File version id*");
    }

    [Fact]
    public void Build_UnknownVariant_Throws()
    {
        var act = () => ThumbnailStorageKeyBuilder.Build(DriveId, VersionId, "huge", ThumbnailFormats.WebP);
        act.Should().Throw<ArgumentException>().WithMessage("*Unknown thumbnail variant*'huge'*");
    }

    [Fact]
    public void Build_UnknownFormat_Throws()
    {
        var act = () => ThumbnailStorageKeyBuilder.Build(DriveId, VersionId, ThumbnailVariants.Thumb, "avif");
        act.Should().Throw<ArgumentException>().WithMessage("*Unknown thumbnail format*'avif'*");
    }
}

public sealed class ThumbnailVariantsTests
{
    [Theory]
    [InlineData("thumb", 256)]
    [InlineData("small", 512)]
    [InlineData("medium", 1024)]
    public void EdgePixelsFor_Known(string variant, int expected)
    {
        ThumbnailVariants.EdgePixelsFor(variant).Should().Be(expected);
    }

    [Fact]
    public void EdgePixelsFor_Unknown_Throws()
    {
        var act = () => ThumbnailVariants.EdgePixelsFor("huge");
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("thumb", true)]
    [InlineData("small", true)]
    [InlineData("medium", true)]
    [InlineData("huge", false)]
    [InlineData("THUMB", false)] // case-sensitive — DB column is varchar; canonical is lowercase
    [InlineData("", false)]
    public void IsKnown_Cases(string variant, bool expected)
    {
        ThumbnailVariants.IsKnown(variant).Should().Be(expected);
    }

    [Fact]
    public void All_Contains_ThreeVariants_InOrder()
    {
        ThumbnailVariants.All.Should().Equal("thumb", "small", "medium");
    }
}

public sealed class ThumbnailFormatsTests
{
    [Theory]
    [InlineData("webp", true)]
    [InlineData("jpeg", true)]
    [InlineData("png", false)]
    [InlineData("WEBP", false)]
    [InlineData("", false)]
    public void IsKnown_Cases(string format, bool expected)
    {
        ThumbnailFormats.IsKnown(format).Should().Be(expected);
    }
}
