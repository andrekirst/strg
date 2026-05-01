using System.ComponentModel.DataAnnotations;

namespace Strg.Plugin.Abstractions.Plugins;

/// <summary>
/// Deserialised representation of a <c>strg-plugin.json</c> file shipped in a plugin package.
/// All properties are <c>init</c>-only: a manifest is parsed once at startup and treated as
/// immutable thereafter, so the host can keep references to it across the loader, DI graph,
/// and admin UI without worrying about a plugin mutating its own metadata after registration.
///
/// <para>Validation lives in <see cref="PluginManifestValidator"/> — the DataAnnotation
/// attributes on this type only express the rules a single field can self-check (presence,
/// SemVer shape). Cross-field rules and security-critical pattern checks (reverse-DNS id,
/// filename-only entry point, known plugin type) run in the validator.</para>
/// </summary>
public sealed record PluginManifest
{
    /// <summary>
    /// Reverse-DNS plugin identifier (e.g. <c>"com.example.my-plugin"</c>). Acts as the stable
    /// key used by the operator-supplied <c>"Plugins"</c> allowlist and by the loader's plugin
    /// cache directory. Reverse-DNS form is enforced by <see cref="PluginManifestValidator"/>
    /// (no path separators, no dots-only segments) — required to keep this safe to use as a
    /// directory name on disk.
    /// </summary>
    [Required]
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable plugin name; surfaced to the admin UI.</summary>
    [Required]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// SemVer version of the plugin itself (e.g. <c>"1.0.0"</c> or <c>"1.0.0-beta.1"</c>). The
    /// regex anchors both ends to reject trailing garbage; <see cref="PluginManifestValidator"/>
    /// re-parses the value numerically for ordering.
    /// </summary>
    [Required]
    [RegularExpression(@"^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$")]
    public string Version { get; init; } = string.Empty;

    /// <summary>Short prose description; surfaced to the admin UI. Optional.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Author or vendor identifier; surfaced to the admin UI. Optional.</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>
    /// Lowest strg host version that satisfies this plugin's compatibility window. Compared
    /// numerically by <see cref="PluginManifestValidator.IsCompatible"/>; a plugin listing
    /// <c>"0.2.0"</c> here will be rejected by a <c>0.1.x</c> host.
    /// </summary>
    [Required]
    public string MinStrgVersion { get; init; } = string.Empty;

    /// <summary>
    /// Optional upper bound. <see langword="null"/> (or absent in JSON) means "no upper bound" —
    /// the plugin is compatible with every host at or above <see cref="MinStrgVersion"/>.
    /// </summary>
    public string? MaxStrgVersion { get; init; }

    /// <summary>
    /// Filename of the entry-point DLL inside the plugin package (e.g.
    /// <c>"Strg.Plugin.Example.dll"</c>). MUST be a bare filename with no directory components;
    /// <see cref="PluginManifestValidator"/> enforces this — the loader resolves the file
    /// relative to the configured plugin directory and any path components would let a malicious
    /// manifest escape the plugin sandbox at load time.
    /// </summary>
    [Required]
    public string EntryPoint { get; init; } = string.Empty;

    /// <summary>
    /// One of the values in <see cref="PluginTypes.KnownTypes"/>. Determines which contract the
    /// loader expects the entry-point assembly to implement.
    /// </summary>
    [Required]
    public string PluginType { get; init; } = string.Empty;

    /// <summary>Optional homepage URL; informational only.</summary>
    public string? Homepage { get; init; }

    /// <summary>Optional SPDX license identifier; informational only.</summary>
    public string? License { get; init; }

    /// <summary>
    /// Capability strings the plugin declares it needs (e.g. <c>"storage.read"</c>,
    /// <c>"storage.write"</c>). Enforced at runtime in v0.2 by the
    /// <c>PermissionEnforcingPluginProxy</c>; in v0.1 the values are recorded for forward
    /// compatibility but no enforcement runs yet.
    /// </summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];
}
