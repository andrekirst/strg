using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Strg.Api.Plugins;
using Strg.Plugin.Abstractions.Plugins;
using Xunit;

namespace Strg.Api.Tests.Plugins;

/// <summary>
/// STRG-089 — exercises <see cref="PluginsConfiguration.AddStrgPluginConfiguration"/> end-to-end.
/// The architectural guarantee under test is the TC-005 contract: plugins NOT listed in the
/// <c>"Plugins"</c> config array must not surface as <see cref="PluginConfig"/> instances even
/// when their files are present on disk. The test exercises the full binding + validation path
/// rather than just the raw <see cref="IConfiguration"/> binder so a regression in the validation
/// rules is caught here, not in production startup.
/// </summary>
public sealed class PluginConfigBindingTests
{
    // TC-005 — only entries in the "Plugins" config array are surfaced.
    [Fact]
    public void Bind_OnlyConfiguredEntries_AreRegistered()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            ["Plugins:0:Id"] = "com.example.configured",
            ["Plugins:0:Path"] = "/opt/strg/plugins/configured/",
        });
        var services = new ServiceCollection();

        services.AddStrgPluginConfiguration(configuration);

        var registered = services.BuildServiceProvider().GetRequiredService<IReadOnlyList<PluginConfig>>();
        registered.Should().HaveCount(1);
        registered[0].Id.Should().Be("com.example.configured");
        registered[0].Path.Should().Be("/opt/strg/plugins/configured/");
    }

    [Fact]
    public void Bind_MissingSection_RegistersEmptyList()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>());
        var services = new ServiceCollection();

        services.AddStrgPluginConfiguration(configuration);

        var registered = services.BuildServiceProvider().GetRequiredService<IReadOnlyList<PluginConfig>>();
        registered.Should().BeEmpty();
    }

    [Fact]
    public void Bind_InvalidId_ThrowsAtStartup()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            ["Plugins:0:Id"] = "evil/../path",
            ["Plugins:0:Path"] = "/opt/strg/plugins/evil/",
        });
        var services = new ServiceCollection();

        var act = () => services.AddStrgPluginConfiguration(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*reverse-DNS*");
    }

    [Fact]
    public void Bind_EmptyPath_ThrowsAtStartup()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            ["Plugins:0:Id"] = "com.example.configured",
            ["Plugins:0:Path"] = "",
        });
        var services = new ServiceCollection();

        var act = () => services.AddStrgPluginConfiguration(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Path*");
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
