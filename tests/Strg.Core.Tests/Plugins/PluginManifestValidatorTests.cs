namespace Strg.Core.Tests.Plugins;

using FluentAssertions;
using Strg.Plugin.Abstractions.Plugins;
using Xunit;

public sealed class PluginManifestValidatorTests
{
    private static PluginManifest BaseManifest() => new()
    {
        Id = "com.example.my-plugin",
        Name = "My Plugin",
        Version = "1.0.0",
        MinStrgVersion = "0.1.0",
        EntryPoint = "Strg.Plugin.Example.dll",
        PluginType = PluginTypes.Storage,
    };

    // TC-002 — plugin requiring 0.2.0 not loaded by 0.1.0 strg.
    [Fact]
    public void IsCompatible_HostBelowMin_ReturnsFalse()
    {
        var manifest = BaseManifest() with { MinStrgVersion = "0.2.0" };

        PluginManifestValidator.IsCompatible(manifest, currentStrgVersion: "0.1.0").Should().BeFalse();
    }

    [Fact]
    public void IsCompatible_HostAtMin_ReturnsTrue()
    {
        var manifest = BaseManifest() with { MinStrgVersion = "0.2.0" };

        PluginManifestValidator.IsCompatible(manifest, currentStrgVersion: "0.2.0").Should().BeTrue();
    }

    [Fact]
    public void IsCompatible_HostAboveMaxBound_ReturnsFalse()
    {
        var manifest = BaseManifest() with { MinStrgVersion = "0.1.0", MaxStrgVersion = "0.2.0" };

        PluginManifestValidator.IsCompatible(manifest, currentStrgVersion: "0.3.0").Should().BeFalse();
    }

    [Fact]
    public void IsCompatible_NullMaxBound_NoUpperLimit()
    {
        var manifest = BaseManifest() with { MinStrgVersion = "0.1.0", MaxStrgVersion = null };

        PluginManifestValidator.IsCompatible(manifest, currentStrgVersion: "99.0.0").Should().BeTrue();
    }

    [Fact]
    public void IsCompatible_MalformedVersion_Throws()
    {
        var manifest = BaseManifest() with { MinStrgVersion = "not-a-version" };

        var act = () => PluginManifestValidator.IsCompatible(manifest, currentStrgVersion: "0.1.0");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Validate_IdWithPathChars_ReturnsError()
    {
        // The id is later used as a directory name; the security checklist requires that
        // path separators and ".." segments cannot smuggle through deserialization.
        var manifest = BaseManifest() with { Id = "com.example/../evil" };

        PluginManifestValidator.Validate(manifest, out var errors).Should().BeFalse();
        errors.Should().Contain(e => e.Contains("reverse-DNS"));
    }

    [Fact]
    public void Validate_IdSingleSegment_ReturnsError()
    {
        var manifest = BaseManifest() with { Id = "plugin" };

        PluginManifestValidator.Validate(manifest, out var errors).Should().BeFalse();
        errors.Should().Contain(e => e.Contains("reverse-DNS"));
    }

    [Theory]
    [InlineData("../Strg.Plugin.Example.dll")]
    [InlineData("subdir/Strg.Plugin.Example.dll")]
    [InlineData("subdir\\Strg.Plugin.Example.dll")]
    [InlineData("..")]
    public void Validate_EntryPointWithPathComponent_ReturnsError(string entryPoint)
    {
        var manifest = BaseManifest() with { EntryPoint = entryPoint };

        PluginManifestValidator.Validate(manifest, out var errors).Should().BeFalse();
        errors.Should().Contain(e => e.Contains("entryPoint"));
    }

    [Fact]
    public void Validate_AllKnownPluginTypes_AreAccepted()
    {
        // Pin the public catalogue: every value in PluginTypes.KnownTypes must round-trip
        // through the validator. A regression here is the failure mode where a new type is
        // added to the const surface but forgotten in KnownTypes (or vice versa).
        foreach (var type in PluginTypes.KnownTypes)
        {
            var manifest = BaseManifest() with { PluginType = type };
            PluginManifestValidator.Validate(manifest, out _).Should().BeTrue($"plugin type '{type}' is in KnownTypes");
        }
    }

    [Fact]
    public void IsValidPluginId_AcceptsReverseDns()
    {
        PluginManifestValidator.IsValidPluginId("com.example.my-plugin").Should().BeTrue();
        PluginManifestValidator.IsValidPluginId("io.acme.search-provider").Should().BeTrue();
    }

    [Theory]
    [InlineData("com.example/../evil")]
    [InlineData("plugin")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("Com.Example.Plugin")] // uppercase rejected — directory case-sensitivity on linux
    [InlineData("com..example")]       // empty middle segment
    public void IsValidPluginId_RejectsBadIds(string id)
    {
        PluginManifestValidator.IsValidPluginId(id).Should().BeFalse();
    }
}
