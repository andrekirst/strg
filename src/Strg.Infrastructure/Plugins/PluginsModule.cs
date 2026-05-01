using Microsoft.Extensions.DependencyInjection;

namespace Strg.Infrastructure.Plugins;

/// <summary>
/// Stub for the v0.2 plugin host. The contract surface in <c>Strg.Plugin.Abstractions</c> is
/// sufficient for in-tree plugins compiled into the host today; the dynamic-load story arrives
/// in v0.2 and will land here. Reserves the extension method that <c>Program.cs</c> will call
/// once the loader and proxy ship.
/// </summary>
public static class PluginsModule
{
    public static IServiceCollection AddStrgPlugins(this IServiceCollection services)
    {
        // v0.2: AssemblyLoadContext loader — discover plugin assemblies under
        // {ContentRoot}/plugins/*.dll, load each into a per-plugin AssemblyLoadContext so
        // plugin dependencies don't leak into the host graph and plugins can be hot-reloaded
        // by recycling the context.

        // v0.2: PermissionEnforcingPluginProxy — wrap each resolved IStrgPlugin in a dispatch
        // proxy that checks the calling tenant's plugin-permissions row before forwarding the
        // call. The proxy is the single chokepoint for plugin authorization (avoids per-
        // interface boilerplate in every plugin contract).

        return services;
    }
}
