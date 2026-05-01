namespace Strg.Plugin.Abstractions.Endpoints;

/// <summary>
/// Marker contract for plugins that contribute HTTP endpoints. The host reserves
/// <see cref="MountPath"/> as the URL prefix before invoking the plugin's
/// <see cref="IStrgPlugin.ConfigureEndpoints"/>; collisions across plugins are detected at
/// registration time.
///
/// <para><b>Convention.</b> Leading slash, no trailing slash; e.g. <c>"/plugins/my-plugin"</c>.
/// Plugins SHOULD namespace under <c>/plugins/{name}</c> to avoid colliding with the host's
/// reserved REST (<c>/api/...</c>), GraphQL (<c>/graphql</c>), or WebDAV (<c>/webdav/...</c>)
/// surfaces.</para>
/// </summary>
public interface IEndpointModule : IStrgPlugin
{
    /// <summary>The URL prefix the host reserves for this plugin's routes.</summary>
    string MountPath { get; }
}
