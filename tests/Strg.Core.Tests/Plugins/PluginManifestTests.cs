namespace Strg.Core.Tests.Plugins;

using System.Text.Json;
using FluentAssertions;
using Strg.Plugin.Abstractions.Plugins;
using Xunit;

public sealed class PluginManifestTests
{
    private const string ValidJson =
        """
        {
          "id": "com.example.my-plugin",
          "name": "My Plugin",
          "version": "1.0.0",
          "description": "A sample plugin for strg",
          "author": "Example Corp",
          "minStrgVersion": "0.1.0",
          "maxStrgVersion": null,
          "entryPoint": "Strg.Plugin.Example.dll",
          "pluginType": "storage",
          "homepage": "https://example.com/my-plugin",
          "license": "MIT",
          "permissions": ["storage.read", "storage.write"]
        }
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // TC-001 — valid manifest deserializes with every field populated.
    [Fact]
    public void Deserialize_ValidJson_AllFieldsPopulated()
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(ValidJson, JsonOptions);

        manifest.Should().NotBeNull();
        manifest!.Id.Should().Be("com.example.my-plugin");
        manifest.Name.Should().Be("My Plugin");
        manifest.Version.Should().Be("1.0.0");
        manifest.Description.Should().Be("A sample plugin for strg");
        manifest.Author.Should().Be("Example Corp");
        manifest.MinStrgVersion.Should().Be("0.1.0");
        manifest.MaxStrgVersion.Should().BeNull();
        manifest.EntryPoint.Should().Be("Strg.Plugin.Example.dll");
        manifest.PluginType.Should().Be("storage");
        manifest.Homepage.Should().Be("https://example.com/my-plugin");
        manifest.License.Should().Be("MIT");
        manifest.Permissions.Should().Equal("storage.read", "storage.write");

        PluginManifestValidator.Validate(manifest, out var errors).Should().BeTrue();
        errors.Should().BeEmpty();
    }

    // TC-003 — missing entryPoint surfaces a validation error.
    [Fact]
    public void Validate_MissingEntryPoint_ReturnsError()
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            """
            {
              "id": "com.example.my-plugin",
              "name": "My Plugin",
              "version": "1.0.0",
              "minStrgVersion": "0.1.0",
              "pluginType": "storage"
            }
            """,
            JsonOptions)!;

        PluginManifestValidator.Validate(manifest, out var errors).Should().BeFalse();
        // DataAnnotation [Required] message uses the property name.
        errors.Should().Contain(e => e.Contains("EntryPoint"));
    }

    // TC-004 — unknown pluginType is rejected.
    [Fact]
    public void Validate_UnknownPluginType_ReturnsError()
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            """
            {
              "id": "com.example.my-plugin",
              "name": "My Plugin",
              "version": "1.0.0",
              "minStrgVersion": "0.1.0",
              "entryPoint": "Strg.Plugin.Example.dll",
              "pluginType": "magic"
            }
            """,
            JsonOptions)!;

        PluginManifestValidator.Validate(manifest, out var errors).Should().BeFalse();
        errors.Should().Contain(e => e.Contains("'magic'"));
    }

    [Fact]
    public void Validate_MalformedVersion_ReturnsError()
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            """
            {
              "id": "com.example.my-plugin",
              "name": "My Plugin",
              "version": "1.0",
              "minStrgVersion": "0.1.0",
              "entryPoint": "Strg.Plugin.Example.dll",
              "pluginType": "storage"
            }
            """,
            JsonOptions)!;

        PluginManifestValidator.Validate(manifest, out var errors).Should().BeFalse();
        errors.Should().Contain(e => e.Contains("Version"));
    }
}
