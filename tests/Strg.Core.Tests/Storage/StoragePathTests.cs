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

    [Fact]
    public void Parse_EmptyString_Throws()
    {
        var act = () => StoragePath.Parse(string.Empty);
        act.Should().Throw<StoragePathException>();
    }

    [Fact]
    public void Parse_WhitespaceOnly_Throws()
    {
        var act = () => StoragePath.Parse("   ");
        act.Should().Throw<StoragePathException>();
    }

    [Fact]
    public void Parse_PercentEncodedSpace_Throws()
    {
        // %20 decodes to a single space character. The empty/whitespace gate runs AFTER
        // Uri.UnescapeDataString so encoded variants are caught alongside literal " ".
        // Without that ordering, `%20` would slip past traversal and reserved-name checks
        // and emerge as Value = " ".
        var act = () => StoragePath.Parse("%20");
        act.Should().Throw<StoragePathException>();
    }

    [Fact]
    public void Parse_ReservedNameInMiddleSegment_Throws()
    {
        // Windows reserves CON/PRN/AUX/NUL/COMn/LPTn at any path level — not just the
        // filename. The previous last-segment-only check let archives/CON/secret.txt
        // through, which is an OS-invalid path on Windows hosts.
        var act = () => StoragePath.Parse("archives/CON/secret.txt");
        act.Should().Throw<StoragePathException>();
    }

    [Fact]
    public void Parse_ReservedNameAsLeadingDirectory_Throws()
    {
        var act = () => StoragePath.Parse("CON/file.txt");
        act.Should().Throw<StoragePathException>();
    }

    [Fact]
    public void Default_StoragePath_GetHashCode_DoesNotThrow()
    {
        // `default(StoragePath)` has Value == null because the private constructor is
        // never called on the zero-initialised struct. Without the null guard,
        // GetHashCode NREd via StringComparer.OrdinalIgnoreCase.GetHashCode(null) — a
        // latent failure mode for any caller that puts a failed TryParse result into a
        // HashSet/Dictionary key.
        var defaultPath = default(StoragePath);
        var act = () => defaultPath.GetHashCode();
        act.Should().NotThrow();
    }

    [Fact]
    public void Default_StoragePath_ToString_ReturnsNonNull()
    {
        // Returning null from ToString is an antipattern that crashes log formatters,
        // string interpolation, and JSON serialisation. The null-coalescing guard turns
        // default(StoragePath) into a safe empty-string sentinel for those code paths.
        var defaultPath = default(StoragePath);
        defaultPath.ToString().Should().NotBeNull();
    }

    [Fact]
    public void Parse_HotPath_DoesNotAllocate()
    {
        // TC-008: 100k calls to Parse with a valid clean path must not introduce per-call
        // heap allocations. Pins IsReservedName against regressions where someone reintroduces
        // p.Split('/') / segment.Split('.') (each round-trip would allocate two arrays plus
        // their substrings — ~100k * (24+ bytes) = >2 MB easily). Tight bound (<1 KB total)
        // makes the failure mode obvious if anyone reverts the span-based rewrite.
        const string path = "docs/report.pdf";

        for (var i = 0; i < 1000; i++)
        {
            StoragePath.Parse(path);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100_000; i++)
        {
            StoragePath.Parse(path);
        }
        var after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).Should().BeLessThan(1024);
    }
}
