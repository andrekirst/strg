using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
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

        // STRG-073 — in-process IMemoryCache shared with any other consumer in the same host.
        // IServiceCollection.AddMemoryCache is idempotent (calls TryAddSingleton internally), so a
        // second caller adding it elsewhere is a no-op rather than a duplicate-registration error.
        services.AddMemoryCache();

        // STRG-073 — Singleton: the cache is process-wide and its side-index must survive across
        // requests for InvalidateUser to work correctly. Scoped would discard the side-index at
        // the end of every request, making eviction-on-password-change unenforceable.
        services.AddSingleton<IWebDavJwtCache, WebDavJwtCache>();

        // STRG-073 — named "oidc" client consumed exclusively by BasicAuthJwtBridgeMiddleware.
        //
        // STRG-073 fold-in #2 (security-reviewer baseline #4) — BaseAddress is resolved from the
        // ACTUAL running Kestrel binding via IServer.Features, not from IConfiguration or
        // WebDavOptions. Any config-bound base address is a credential-exfiltration vector: a
        // deploy-time typo would redirect every WebDAV user's cleartext password to the
        // misconfigured URL. By reading from IServerAddressesFeature we guarantee the bridge
        // only ever POSTs to the process's OWN token endpoint.
        //
        // The callback fires at HttpClient resolve-time; by then Kestrel has finished binding
        // (StartAsync runs before any request arrives), so Features.Addresses is populated.
        // Wildcard bindings ("http://+:5000", "http://*:5000") are normalized to loopback so the
        // request targets 127.0.0.1 directly rather than relying on DNS or external routing.
        //
        // A longer-than-default timeout would let a hung token endpoint pile up WebDAV requests
        // past the cache-miss fanout — stick with the 100-second default HttpClient budget.
        services.AddHttpClient(BasicAuthJwtBridgeMiddleware.OidcHttpClientName, (sp, client) =>
        {
            var server = sp.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
            if (addresses is null || addresses.Count == 0)
            {
                throw new InvalidOperationException(
                    "STRG-073: IServerAddressesFeature has no addresses — the WebDAV Basic-Auth " +
                    "bridge cannot resolve the in-process /connect/token endpoint. This indicates " +
                    "the Kestrel server has not started listening; the named 'oidc' HttpClient " +
                    "must not be resolved before application startup completes.");
            }

            // Prefer http:// over https:// for loopback to avoid TLS handshake + cert-validation
            // against a self-signed dev cert. If only https:// is bound we still have to use it,
            // but ServerCertificateCustomValidationCallback would be a bigger foot-gun than a TLS
            // round-trip on the same host.
            var raw = addresses.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                ?? addresses.First();
            var normalized = raw
                .Replace("://*", "://127.0.0.1", StringComparison.Ordinal)
                .Replace("://+", "://127.0.0.1", StringComparison.Ordinal)
                .Replace("://[::]", "://[::1]", StringComparison.Ordinal);
            client.BaseAddress = new Uri(normalized);
        });
        return services;
    }
}
