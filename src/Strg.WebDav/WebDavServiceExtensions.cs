using Microsoft.Extensions.DependencyInjection;

namespace Strg.WebDav;

/// <summary>
/// DI wiring for the WebDAV stack. Called from <c>Program.cs</c> on the main
/// <see cref="IServiceCollection"/> so the services are visible inside the branched
/// <c>/dav</c> pipeline alongside the rest of the app's registrations.
///
/// <para>Per STRG-067/STRG-068: <see cref="IDriveResolver"/> plus the <see cref="IStrgWebDavStore"/>
/// bridge are all that the foundation + store slices need. Downstream tickets layer on:
/// <list type="bullet">
///   <item><description>STRG-069 — PROPFIND + PROPPATCH parser for client-supplied property
///     requests.</description></item>
///   <item><description>STRG-070 — <c>ILockManager</c> backed by the <c>file_locks</c>
///     table.</description></item>
///   <item><description>STRG-071 / STRG-072 — PUT / MKCOL / DELETE / COPY / MOVE write-side
///     handlers.</description></item>
/// </list>
/// </para>
/// </summary>
public static class WebDavServiceExtensions
{
    public static IServiceCollection AddStrgWebDav(this IServiceCollection services)
    {
        services.AddScoped<IDriveResolver, DriveResolver>();

        // Scoped because StrgWebDavStore depends on StrgDbContext (scoped). A singleton
        // registration here would capture a disposed context after the first request.
        services.AddScoped<IStrgWebDavStore, StrgWebDavStore>();
        return services;
    }
}
