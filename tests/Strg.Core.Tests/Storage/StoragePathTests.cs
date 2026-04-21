namespace Strg.Core.Tests.Storage;

using FluentAssertions;
using Strg.Core.Storage;
using Xunit;

public sealed class StoragePathTests
{
    [Fact]
    public void Parse_TraversalWithDotDotSlash_Throws()
    {
        var act = () => StoragePath.Parse("../../etc/passwd");
        act.Should().Throw<StoragePathException>();
    }

    [Fact]
    public void Parse_TraversalWithDotDot_Throws()
    {
        var act = () => StoragePath.Parse("../secret");
        act.Should().Throw<StoragePathException>();
    }

    [Fact]
    public void Parse_ValidRelativePath_Succeeds()
    {
        var path = StoragePath.Parse("docs/report.pdf");
        path.Value.Should().Be("docs/report.pdf");
    }

    [Fact]
    public void Parse_AbsolutePath_Throws()
    {
        var act = () => StoragePath.Parse("/absolute/path");
        act.Should().Throw<StoragePathException>();
    }

    [Fact]
    public void Parse_DoubleSlash_Throws()
    {
        var act = () => StoragePath.Parse("docs//double-slash");
        act.Should().Throw<StoragePathException>();
    }

    [Fact]
    public void Parse_WindowsReservedName_Throws()
    {
        var act = () => StoragePath.Parse("CON");
        act.Should().Throw<StoragePathException>();
    }

    [Fact]
    public void Parse_BackslashPath_NormalizesToForwardSlash()
    {
        var path = StoragePath.Parse("docs\\report.pdf");
        path.Value.Should().Be("docs/report.pdf");
    }

    [Fact]
    public void Parse_TrailingSlash_IsTrimmed()
    {
        var path = StoragePath.Parse("docs/report.pdf/");
        path.Value.Should().Be("docs/report.pdf");
    }

    [Fact]
    public void Parse_NullByte_Throws()
    {
        var act = () => StoragePath.Parse("file\0.txt");
        act.Should().Throw<StoragePathException>();
    }

    [Fact]
    public void TryParse_TraversalPath_ReturnsFalse()
    {
        var result = StoragePath.TryParse("../../etc/passwd", out _);
        result.Should().BeFalse();
    }

    [Fact]
    public void TryParse_ValidPath_ReturnsTrueAndSetsValue()
    {
        var result = StoragePath.TryParse("docs/file.txt", out var path);
        result.Should().BeTrue();
        path.Value.Should().Be("docs/file.txt");
    }

    [Fact]
    public void Parse_UrlEncodedTraversal_Throws()
    {
        var act = () => StoragePath.Parse("%2E%2E/secret");
        act.Should().Throw<StoragePathException>();
    }

    [Fact]
    public void Parse_PercentEncodedNullByte_Throws()
    {
        // %00 survives a raw-string null-byte check and only becomes \0 after URL-decode.
        // The fix decodes first, so this must throw.
        var act = () => StoragePath.Parse("legal%00.txt");
        act.Should().Throw<StoragePathException>();
    }

    [Fact]
    public void Parse_UncBackslashPath_Throws()
    {
        // \\server\share becomes //server/share after backslash normalization, which trips
        // the "//" traversal rule. Without pre-normalization, the backslash form would
        // slip past ContainsTraversal entirely.
        var act = () => StoragePath.Parse(@"\\server\share\file.txt");
        act.Should().Throw<StoragePathException>();
    }
}
