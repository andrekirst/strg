namespace Strg.Plugin.Abstractions.Plugins;

/// <summary>
/// One entry of the operator-supplied <c>"Plugins"</c> allowlist in <c>appsettings.json</c>.
/// Plugins are loaded ONLY when their id appears in this list — there is no automatic discovery
/// from a plugin directory, so a stray DLL on disk will not run unless the operator has
/// explicitly opted into it via configuration.
///
/// <para><b>Shape note.</b> A class with <c>init</c>-only properties (rather than a positional
/// record) so <see cref="Microsoft.Extensions.Configuration.ConfigurationBinder"/> can hydrate
/// it without knowing about positional constructors — the binder writes through the public
/// setters, which positional records do not expose.</para>
/// </summary>
public sealed class PluginConfig
{
    /// <summary>
    /// Reverse-DNS plugin identifier. MUST match the <see cref="PluginManifest.Id"/> of the
    /// plugin package found at <see cref="Path"/>. The id is validated through
    /// <see cref="PluginManifestValidator.IsValidPluginId"/> at startup so a typo or path-
    /// injection attempt fails fast rather than silently registering nothing.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Filesystem directory containing the plugin's package (the <c>strg-plugin.json</c> and
    /// the entry-point DLL). Validated as non-empty at startup; the loader (v0.2) is responsible
    /// for refusing paths that escape its plugin root.
    /// </summary>
    public string Path { get; init; } = string.Empty;
}
