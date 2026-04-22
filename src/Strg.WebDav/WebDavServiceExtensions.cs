using Microsoft.Extensions.DependencyInjection;

namespace Strg.WebDav;

/// <summary>
/// DI wiring for the WebDAV stack. Called from <c>Program.cs</c> on the main
/// <see cref="IServiceCollection"/> so the services are visible inside the branched
/// <c>/dav</c> pipeline alongside the rest of the app's registrations.
///
/// <para>Per STRG-067: <see cref="IDriveResolver"/> is the only concrete dependency this
/// foundation slice adds. Downstream tickets layer on:
/// <list type="bullet">
///   <item><description>STRG-068 — <c>IWebDavDispatcher</c> + <c>IStrgWebDavStore</c> for the
///     actual per-verb handlers.</description></item>
///   <item><description>STRG-070 — <c>ILockManager</c> backed by the <c>file_locks</c>
///     table.</description></item>
/// </list>
/// Deliberately not registering those here yet — a stub <c>AddScoped&lt;IWebDavDispatcher, StubWebDavDispatcher&gt;</c>
/// would be dead code the moment STRG-068 lands, and the 501 response in
/// <see cref="StrgWebDavMiddleware"/> already gives clients a truthful "not yet implemented"
/// signal for non-OPTIONS verbs.</para>
/// </summary>
public static class WebDavServiceExtensions
{
    public static IServiceCollection AddStrgWebDav(this IServiceCollection services)
    {
        services.AddScoped<IDriveResolver, DriveResolver>();
        return services;
    }
}
