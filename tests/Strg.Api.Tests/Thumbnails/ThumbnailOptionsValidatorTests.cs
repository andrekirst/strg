using FluentAssertions;
using Strg.Infrastructure.Thumbnails;
using Xunit;

namespace Strg.Api.Tests.Thumbnails;

/// <summary>
/// STRG-334 — pins fail-fast startup validation for <see cref="ThumbnailOptions"/>. Each rule
/// has both a positive (default config passes) and a negative (each invalid input rejected
/// with a named field in the message) coverage.
/// </summary>
public sealed class ThumbnailOptionsValidatorTests
{
    private static readonly ThumbnailOptionsValidator Validator = new();

    [Fact]
    public void Defaults_Pass()
    {
        var opt = new ThumbnailOptions();
        var result = Validator.Validate(name: null, opt);
        result.Succeeded.Should().BeTrue(because: $"failures: {string.Join(", ", result.Failures ?? [])}");
    }

    [Fact]
    public void EmptyVariants_Fails()
    {
        var opt = new ThumbnailOptions { Variants = Array.Empty<string>() };
        var result = Validator.Validate(null, opt);
        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("Variants");
    }

    [Fact]
    public void UnknownVariant_Fails_AndNamesIt()
    {
        var opt = new ThumbnailOptions { Variants = new[] { "small", "huge" } };
        var result = Validator.Validate(null, opt);
        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("'huge'");
    }

    [Fact]
    public void ZeroMaxSourceSize_Fails()
    {
        var opt = new ThumbnailOptions { MaxSourceSizeBytes = 0 };
        var result = Validator.Validate(null, opt);
        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("MaxSourceSizeBytes");
    }

    [Fact]
    public void NegativeMaxPixelArea_Fails()
    {
        var opt = new ThumbnailOptions { MaxPixelArea = -1 };
        var result = Validator.Validate(null, opt);
        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("MaxPixelArea");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(601)]
    [InlineData(99999)]
    public void TimeoutOutOfRange_Fails(int seconds)
    {
        var opt = new ThumbnailOptions { GenerationTimeoutSeconds = seconds };
        var result = Validator.Validate(null, opt);
        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("GenerationTimeoutSeconds");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(82)]
    [InlineData(600)]
    public void TimeoutInRange_Passes(int seconds)
    {
        var opt = new ThumbnailOptions { GenerationTimeoutSeconds = seconds };
        var result = Validator.Validate(null, opt);
        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(101)]
    public void WebPQualityOutOfRange_Fails(int quality)
    {
        var opt = new ThumbnailOptions { WebPQuality = quality };
        var result = Validator.Validate(null, opt);
        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("WebPQuality");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(82)]
    [InlineData(100)]
    public void WebPQualityInRange_Passes(int quality)
    {
        var opt = new ThumbnailOptions { WebPQuality = quality };
        var result = Validator.Validate(null, opt);
        result.Succeeded.Should().BeTrue();
    }
}
