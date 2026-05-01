using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Strg.Plugin.Abstractions.Plugins;

/// <summary>
/// Static helpers for validating a <see cref="PluginManifest"/> and for deciding whether a
/// host version satisfies the manifest's compatibility window. Lives in this contract layer
/// (rather than the loader) so plugin authors can run the same checks at build time that the
/// host runs at startup — same code path, no drift between the two surfaces.
/// </summary>
public static partial class PluginManifestValidator
{
    /// <summary>
    /// Reverse-DNS pattern for <see cref="PluginManifest.Id"/>. Each segment must start with a
    /// letter or digit, may contain hyphens internally, and must not end on a hyphen; segments
    /// are joined with a single dot, and at least two segments are required (so <c>"plugin"</c>
    /// alone fails — the leading reverse-DNS authority is mandatory). The pattern intentionally
    /// rejects path separators (<c>/</c>, <c>\</c>), dots-only segments (<c>".."</c>), and
    /// whitespace, which is the security-checklist guarantee — the id is later used as a
    /// directory name on disk, and any of those would let a malicious manifest escape the
    /// plugin cache root.
    /// </summary>
    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)+$")]
    private static partial Regex ReverseDnsRegex();

    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)(?:-[A-Za-z0-9.-]+)?$")]
    private static partial Regex SemVerRegex();

    /// <summary>
    /// Runs the DataAnnotation attributes on <see cref="PluginManifest"/> plus three layered
    /// checks the attributes can't express on their own:
    /// <list type="number">
    ///   <item><see cref="PluginManifest.Id"/> matches the reverse-DNS pattern (no path
    ///         separators, no <c>".."</c> segments) — Security checklist guarantee.</item>
    ///   <item><see cref="PluginManifest.EntryPoint"/> is a bare filename with no directory
    ///         components — Security checklist guarantee.</item>
    ///   <item><see cref="PluginManifest.PluginType"/> is one of <see cref="PluginTypes.KnownTypes"/>.</item>
    /// </list>
    /// Returns <see langword="true"/> only when both layers pass; <paramref name="errors"/> is
    /// populated with one human-readable message per violation either way.
    /// </summary>
    public static bool Validate(PluginManifest manifest, out IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var collected = new List<string>();
        var dataAnnotationResults = new List<ValidationResult>();
        var context = new ValidationContext(manifest);

        // validateAllProperties:true — the default would short-circuit on the first failure per
        // property, but operators benefit from the full list (a manifest can be missing several
        // required fields at once on first author).
        Validator.TryValidateObject(manifest, context, dataAnnotationResults, validateAllProperties: true);
        foreach (var result in dataAnnotationResults)
        {
            collected.Add(result.ErrorMessage ?? "Unknown manifest validation error.");
        }

        // The DataAnnotation [Required] check considers an empty string a violation, so the
        // pattern checks below only run when the field is present. Skipping them on a missing
        // field would otherwise re-report "Id is required" twice (once from DataAnnotations,
        // once from the regex mismatch on empty input).
        if (!string.IsNullOrEmpty(manifest.Id) && !IsValidPluginId(manifest.Id))
        {
            collected.Add($"Plugin id '{manifest.Id}' must be reverse-DNS (e.g. 'com.example.my-plugin'); path characters and '..' segments are rejected.");
        }

        if (!string.IsNullOrEmpty(manifest.EntryPoint) && !IsFilenameOnly(manifest.EntryPoint))
        {
            collected.Add($"Plugin entryPoint '{manifest.EntryPoint}' must be a bare filename with no directory components.");
        }

        if (!string.IsNullOrEmpty(manifest.PluginType) && !PluginTypes.KnownTypes.Contains(manifest.PluginType))
        {
            collected.Add($"Plugin type '{manifest.PluginType}' is not one of the known values: {string.Join(", ", PluginTypes.KnownTypes)}.");
        }

        errors = collected;
        return collected.Count == 0;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="currentStrgVersion"/> falls inside
    /// the manifest's <c>[MinStrgVersion, MaxStrgVersion]</c> window (both bounds inclusive).
    /// A <see langword="null"/> <see cref="PluginManifest.MaxStrgVersion"/> means "no upper
    /// bound". Throws <see cref="FormatException"/> if any version string is not parseable as
    /// <c>major.minor.patch</c>.
    /// </summary>
    public static bool IsCompatible(PluginManifest manifest, string currentStrgVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentStrgVersion);

        var current = ParseSemVer(currentStrgVersion);
        var min = ParseSemVer(manifest.MinStrgVersion);

        if (Compare(current, min) < 0)
        {
            return false;
        }

        if (manifest.MaxStrgVersion is null)
        {
            return true;
        }

        var max = ParseSemVer(manifest.MaxStrgVersion);
        return Compare(current, max) <= 0;
    }

    /// <summary>
    /// Reverse-DNS pattern check for <see cref="PluginManifest.Id"/>. Exposed so the
    /// configuration-binding layer (<c>AddStrgPluginConfiguration</c> in Strg.Api) can re-use
    /// the exact same rule on the operator-supplied <c>"Plugins"</c> allowlist — a config entry
    /// whose <c>Id</c> matches no manifest in the eventual catalogue is still required to be a
    /// safe directory name.
    /// </summary>
    public static bool IsValidPluginId(string id)
    {
        return !string.IsNullOrEmpty(id) && ReverseDnsRegex().IsMatch(id);
    }

    private static bool IsFilenameOnly(string entryPoint)
    {
        // Catch every path-component shape: directory separators, alt separators, parent-dir
        // tokens, and any input where Path.GetFileName returns something different from the
        // input. Done as four explicit checks rather than relying on Path.GetFileName alone
        // because Path.GetFileName silently treats inputs ending in a separator as having no
        // filename and returns an empty string — which would equal "" but not equal the
        // original entryPoint, so the equality check below catches it. Keeping the explicit
        // checks makes the intent (and the diagnostic) obvious.
        if (entryPoint.Contains('/') || entryPoint.Contains('\\'))
        {
            return false;
        }
        if (entryPoint == "." || entryPoint == "..")
        {
            return false;
        }
        return string.Equals(Path.GetFileName(entryPoint), entryPoint, StringComparison.Ordinal);
    }

    private static (int Major, int Minor, int Patch) ParseSemVer(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var match = SemVerRegex().Match(version);
        if (!match.Success)
        {
            throw new FormatException($"Version '{version}' is not a valid SemVer string (expected 'major.minor.patch').");
        }

        var major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minor = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var patch = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        return (major, minor, patch);
    }

    private static int Compare((int Major, int Minor, int Patch) left, (int Major, int Minor, int Patch) right)
    {
        var major = left.Major.CompareTo(right.Major);
        if (major != 0)
        {
            return major;
        }
        var minor = left.Minor.CompareTo(right.Minor);
        if (minor != 0)
        {
            return minor;
        }
        return left.Patch.CompareTo(right.Patch);
    }
}
