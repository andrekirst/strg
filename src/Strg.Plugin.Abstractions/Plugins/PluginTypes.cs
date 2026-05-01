using System.Collections.Frozen;

namespace Strg.Plugin.Abstractions.Plugins;

/// <summary>
/// Canonical <c>pluginType</c> values for <see cref="PluginManifest.PluginType"/>. Each constant
/// names the contract surface the manifest's entry-point assembly is expected to implement; the
/// loader uses the value to decide which DI registration path to take when the plugin is
/// activated. <see cref="KnownTypes"/> is the membership oracle for manifest validation — any
/// value outside this set fails <see cref="PluginManifestValidator.Validate"/>.
/// </summary>
public static class PluginTypes
{
    /// <summary>Plugin contributes one or more <c>IStorageProvider</c> implementations.</summary>
    public const string Storage = "storage";

    /// <summary>Plugin contributes an <c>IAuthConnector</c> (LDAP/SAML/OAuth2 adapter).</summary>
    public const string Auth = "auth";

    /// <summary>Plugin contributes an <c>ISearchProvider</c> (Elasticsearch, Meilisearch, …).</summary>
    public const string Search = "search";

    /// <summary>Plugin contributes an <c>IEndpointModule</c> mounted under <c>/plugins/{name}</c>.</summary>
    public const string Endpoint = "endpoint";

    /// <summary>Plugin contributes an <c>IAITagger</c> for automated file tagging.</summary>
    public const string AiTagger = "ai-tagger";

    /// <summary>Plugin contributes an <c>IFederationProvider</c> (e.g. ActivityPub).</summary>
    public const string Federation = "federation";

    /// <summary>Plugin implements multiple contracts; the loader probes the entry assembly for each.</summary>
    public const string Generic = "generic";

    /// <summary>
    /// Frozen ordinal set of the seven canonical types above. <see cref="FrozenSet{T}"/> is used
    /// rather than a plain <see cref="HashSet{T}"/> because the membership lookup runs once per
    /// configured plugin at startup AND every time a manifest is re-validated; FrozenSet's read-
    /// optimised layout keeps this on the cheap branch even as the type list grows.
    /// </summary>
    public static readonly FrozenSet<string> KnownTypes = new[]
    {
        Storage,
        Auth,
        Search,
        Endpoint,
        AiTagger,
        Federation,
        Generic,
    }.ToFrozenSet(StringComparer.Ordinal);
}
