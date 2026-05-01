using FluentAssertions;
using Strg.Core.Services;
using Xunit;

namespace Strg.Core.Tests.Services;

public sealed class MimeSnifferTests
{
    [Fact]
    public void Detect_Jpeg()
    {
        ReadOnlySpan<byte> jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
        MimeSniffer.Detect(jpeg).Should().Be("image/jpeg");
    }

    [Fact]
    public void Detect_Png()
    {
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];
        MimeSniffer.Detect(png).Should().Be("image/png");
    }

    [Theory]
    [InlineData(0x37)] // GIF87a
    [InlineData(0x39)] // GIF89a
    public void Detect_Gif(byte versionByte)
    {
        ReadOnlySpan<byte> gif = [0x47, 0x49, 0x46, 0x38, versionByte, 0x61, 0x00, 0x00];
        MimeSniffer.Detect(gif).Should().Be("image/gif");
    }

    [Fact]
    public void Detect_WebP()
    {
        ReadOnlySpan<byte> webp =
        [
            0x52, 0x49, 0x46, 0x46, // "RIFF"
            0x24, 0x00, 0x00, 0x00, // file size (irrelevant)
            0x57, 0x45, 0x42, 0x50, // "WEBP"
        ];
        MimeSniffer.Detect(webp).Should().Be("image/webp");
    }

    [Fact]
    public void Detect_Pdf()
    {
        ReadOnlySpan<byte> pdf = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37];
        MimeSniffer.Detect(pdf).Should().Be("application/pdf");
    }

    [Fact]
    public void Detect_HeicBrand()
    {
        // bytes 4..7 = "ftyp", brand at 8..11 = "heic"
        ReadOnlySpan<byte> heic =
        [
            0x00, 0x00, 0x00, 0x18,
            0x66, 0x74, 0x79, 0x70,
            0x68, 0x65, 0x69, 0x63,
        ];
        MimeSniffer.Detect(heic).Should().Be("image/heic");
    }

    [Fact]
    public void Detect_MifBrand_AsHeif()
    {
        ReadOnlySpan<byte> heif =
        [
            0x00, 0x00, 0x00, 0x18,
            0x66, 0x74, 0x79, 0x70,
            0x6D, 0x69, 0x66, 0x31,  // "mif1"
        ];
        MimeSniffer.Detect(heif).Should().Be("image/heif");
    }

    [Fact]
    public void Detect_UnknownFtypBrand_ReturnsNull()
    {
        ReadOnlySpan<byte> unknown =
        [
            0x00, 0x00, 0x00, 0x18,
            0x66, 0x74, 0x79, 0x70,
            0x6D, 0x70, 0x34, 0x32,  // "mp42" — not in the whitelist
        ];
        MimeSniffer.Detect(unknown).Should().BeNull();
    }

    [Fact]
    public void Detect_TruncatedJpeg_ReturnsNull()
    {
        ReadOnlySpan<byte> truncated = [0xFF, 0xD8];   // missing third signature byte
        MimeSniffer.Detect(truncated).Should().BeNull();
    }

    [Fact]
    public void Detect_Empty_ReturnsNull()
    {
        MimeSniffer.Detect(ReadOnlySpan<byte>.Empty).Should().BeNull();
    }

    [Fact]
    public void Detect_RandomGarbage_ReturnsNull()
    {
        ReadOnlySpan<byte> garbage = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B];
        MimeSniffer.Detect(garbage).Should().BeNull();
    }

    [Fact]
    public void Detect_RiffWithoutWebpFourCc_ReturnsNull()
    {
        ReadOnlySpan<byte> riffWav =
        [
            0x52, 0x49, 0x46, 0x46,
            0x00, 0x00, 0x00, 0x00,
            0x57, 0x41, 0x56, 0x45,  // "WAVE" — not WebP
        ];
        MimeSniffer.Detect(riffWav).Should().BeNull();
    }
}
