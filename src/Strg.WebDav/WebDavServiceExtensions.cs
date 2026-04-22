using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Strg.WebDav;

/// <summary>
/// DI wiring for the WebDAV stack. Called from <c>Program.cs</c> on the main
/// <see cref="IServiceCollection"/> so the services are visible inside the branched
/// <c>/dav</c> pipeline alongside the rest of the app's registrations.
///
/// <para>Registrations by ticket:
/// <list type="bullet">
///   <item><description>STRG-067/068 — <see cref="IDriveResolver"/> + <see cref="IStrgWebDavStore"/>.</description></item>
///   <item><description>STRG-069 — PROPFIND + PROPPATCH parser (inline in
///     <see cref="WebDavResponseWriter"/>).</description></item>
///   <item><description>STRG-070 — PUT handled through <see cref="IStrgWebDavStore.PutDocumentAsync"/>.</description></item>
///   <item><description>STRG-072 — <see cref="IStrgWebDavLockManager"/> backed by the
///     <c>file_locks</c> table. Own abstraction rather than NWebDav's <c>ILockManager</c> for the
///     same reason <see cref="IStrgWebDavStore"/> exists — avoiding the vulnerable
///     <c>NWebDav.Server.AspNetCore</c> adapter. ArchTest #118 guards the absence.</description></item>
/// </list>
/// </para>
/// </summary>
public static class WebDavServiceExtensions
{
    public static IServiceCollection AddStrgWebDav(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Options binding for the WebDav section. The default values on WebDavOptions are
        // load-bearing (PropfindInfinityMaxItems = 10 000), so a missing WebDav section in
        // appsettings is a no-op rather than a startup failure. Tests override by calling
        // IConfigurationBuilder.AddInMemoryCollection before CreateClient().
        services.AddOptions<WebDavOptions>()
            .Bind(configuration.GetSection(WebDavOptions.SectionName));

        services.AddScoped<IDriveResolver, DriveResolver>();

        // Scoped because StrgWebDavStore depends on StrgDbContext (scoped). A singleton
        // registration here would capture a disposed context after the first request.
        services.AddScoped<IStrgWebDavStore, StrgWebDavStore>();

        // Scoped for the same reason — DbLockManager owns an EF query stream against FileLocks
        // and depends on ITenantContext which is per-request.
        services.AddScoped<IStrgWebDavLockManager, DbLockManager>();
        return services;
    }
}
