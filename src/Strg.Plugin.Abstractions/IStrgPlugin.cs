using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Strg.Plugin.Abstractions;

/// <summary>
/// Base contract every strg plugin MUST implement. Loaded by the v0.2 plugin host into a
/// dedicated <c>AssemblyLoadContext</c> for isolation; at v0.1 only in-tree plugins compiled
/// into the host satisfy this contract — the dynamic-load story arrives in v0.2 alongside the
/// permission-enforcing proxy.
///
/// <para><b>Lifecycle.</b> The host invokes the three lifecycle methods in this exact order,
/// once per plugin instance, on the host's startup thread:
/// <list type="number">
///   <item><see cref="ConfigureServices"/> — DI registrations land into the host's service
///         collection. Use <c>TryAdd*</c> rather than <c>Add*</c> so plugins do not silently
///         clobber host services or other plugins' contributions.</item>
///   <item><see cref="ConfigureEndpoints"/> — route mapping happens after the host has mounted
///         its own routes. Plugins SHOULD mount under <c>/plugins/{name}</c> (see
///         <see cref="Endpoints.IEndpointModule.MountPath"/>) to avoid colliding with the host's
///         REST/GraphQL/WebDAV surface or with other plugins.</item>
///   <item><see cref="InitializeAsync"/> — runs once after the <see cref="IServiceProvider"/>
///         is built and before the host starts accepting traffic. The supplied
///         <c>cancellationToken</c> is the host's startup-cancel signal; plugins MUST observe it
///         to keep host startup responsive.</item>
/// </list></para>
///
/// <para><b>Metadata immutability.</b> <see cref="Name"/>, <see cref="Version"/>,
/// <see cref="Description"/>, and <see cref="Author"/> are read once at load time and surfaced to
/// the admin UI; plugins MUST return stable values (no per-call computation, no environment
/// lookups). <see cref="Version"/> follows SemVer (e.g. <c>"1.4.2"</c> or <c>"0.9.0-beta.1"</c>).</para>
/// </summary>
public interface IStrgPlugin
{
    /// <summary>Stable, human-readable plugin name; surfaced to the admin UI.</summary>
    string Name { get; }

    /// <summary>SemVer string (e.g. <c>"1.4.2"</c>). The host enforces no version policy at v0.1.</summary>
    string Version { get; }

    /// <summary>Short prose description; surfaced to the admin UI.</summary>
    string Description { get; }

    /// <summary>Author or vendor identifier; surfaced to the admin UI.</summary>
    string Author { get; }

    /// <summary>
    /// Registers DI services for this plugin into the host's service collection. Use the
    /// <c>TryAdd*</c> family so the registration does not clobber host services or other plugins.
    /// </summary>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>
    /// Maps HTTP endpoints contributed by this plugin. Plugins SHOULD mount routes under
    /// <c>/plugins/{name}</c> — see <see cref="Endpoints.IEndpointModule.MountPath"/>.
    /// </summary>
    void ConfigureEndpoints(IEndpointRouteBuilder endpoints);

    /// <summary>
    /// Runs once after the host's <see cref="IServiceProvider"/> is built and before the host
    /// accepts traffic. Default implementation is a no-op; plugins that complete all setup in
    /// <see cref="ConfigureServices"/> can omit it. Plugins MUST honour
    /// <paramref name="cancellationToken"/> — it is the host's startup-cancel signal.
    /// </summary>
    Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
