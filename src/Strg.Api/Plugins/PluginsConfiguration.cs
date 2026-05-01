using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Strg.Plugin.Abstractions.Plugins;

namespace Strg.Api.Plugins;

/// <summary>
/// Wires the <c>"Plugins"</c> configuration array into DI. Plugins are loaded ONLY when their
/// id appears in this list — there is no directory scan and no implicit discovery, so this
/// extension is the single point at which the operator's allowlist becomes visible to the rest
/// of the host. The actual loader (<c>AssemblyLoadContext</c>-based isolation +
/// permission-enforcing proxy) ships in v0.2; v0.1 ends with the validated catalogue registered
/// as a singleton so the v0.2 loader has a stable surface to consume.
/// </summary>
public static class PluginsConfiguration
{
    /// <summary>Configuration root key for the plugin allowlist.</summary>
    public const string SectionName = "Plugins";

    /// <summary>
    /// Reads <c>"Plugins"</c> as a list of <see cref="PluginConfig"/>, validates each entry
    /// (reverse-DNS id, non-empty path), and registers the resulting catalogue as a singleton
    /// <see cref="IReadOnlyList{T}"/> of <see cref="PluginConfig"/>. Throws
    /// <see cref="InvalidOperationException"/> at startup on the first invalid entry — fail-fast
    /// is mandatory here because a silently-dropped entry would mean the operator believes
    /// they've enabled a plugin that the host never registers.
    /// </summary>
    public static IServiceCollection AddStrgPluginConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var configs = configuration.GetSection(SectionName).Get<List<PluginConfig>>() ?? [];

        for (var i = 0; i < configs.Count; i++)
        {
            var entry = configs[i];

            if (!PluginManifestValidator.IsValidPluginId(entry.Id))
            {
                throw new InvalidOperationException(
                    $"Plugins[{i}].Id '{entry.Id}' is not a valid reverse-DNS plugin id (e.g. 'com.example.my-plugin').");
            }

            if (string.IsNullOrWhiteSpace(entry.Path))
            {
                throw new InvalidOperationException(
                    $"Plugins[{i}].Path must be a non-empty filesystem path for plugin '{entry.Id}'.");
            }
        }

        services.AddSingleton<IReadOnlyList<PluginConfig>>(configs);
        return services;
    }
}
