using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Validation;
using OpenIddict.Validation.AspNetCore;
using Strg.Infrastructure.Data;

namespace Strg.Infrastructure.Identity;

public static class OpenIddictConfiguration
{
    // Config section + key convention: OpenIddict:SigningCertPath, OpenIddict:SigningCertPassword,
    // OpenIddict:EncryptionCertPath, OpenIddict:EncryptionCertPassword.
    // Env-var binding follows the standard double-underscore form (OpenIddict__SigningCertPath=...).
    private const string SigningCertKeyPrefix = "SigningCert";
    private const string EncryptionCertKeyPrefix = "EncryptionCert";

    public static IServiceCollection AddStrgOpenIddict(
        this IServiceCollection services,
        IConfiguration config,
        bool isDevelopment)
    {
        // Without this, ASP.NET Core has no default scheme to authenticate/challenge against, so
        // every [Authorize]-protected endpoint (controllers AND minimal APIs AND /graphql) throws
        // "No authenticationScheme was specified, and there was no DefaultChallengeScheme found."
        // — surfacing as a 500 instead of the intended 401/403. AddValidation().UseAspNetCore()
        // below registers the scheme but does NOT make it the default; we have to do that here.
        services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

        // Explicit issuer pin. Without this, both the server (when emitting `iss` on tokens) and the
        // validation handler (when building ValidIssuers) fall back to the request's BaseUri =
        // scheme://host/pathbase. Any sub-pipeline mounted via `app.Map("/dav", ...)` (or any
        // reverse-proxy deployment that prepends a path prefix) shifts PathBase and the two sides
        // disagree — token carries iss=http://host/, validation expects iss=http://host/dav — →
        // SecurityTokenInvalidIssuerException → 401 on every /dav request despite a valid bearer.
        //
        // STRG-074 #152 — earlier iterations of this code read ONLY `OpenIddict:Issuer` from
        // IConfiguration. That key is absent from the shipped `appsettings.json` (deliberately — we
        // do not want to ship a hard-coded issuer that escapes from a container's localhost into
        // whatever hostname the config-file template embeds), so the pin was a SILENT no-op in
        // every default deployment: operators launched the host, the /dav sub-pipeline 401'd every
        // bearer, and the error surface was a bearer-validation 401 rather than a configuration
        // error. The only reason integration tests stayed green was that the test factory injected
        // the key in-memory, masking the production silence.
        //
        // Self-detect from the Kestrel binding closes that gap: IServerAddressesFeature exposes
        // the address(es) Kestrel actually bound to, which is the same value we'd want in the
        // `iss` claim whether the host was launched with `ASPNETCORE_URLS=http://+:5000` or
        // `ASPNETCORE_URLS=http://0.0.0.0:80` or the dev-default `http://localhost:5000`. Operators
        // who need a specific canonical issuer (behind a reverse proxy, for example) still set
        // `OpenIddict:Issuer` — the config-key override takes precedence over self-detect below.
        //
        // The `Configure<IConfiguration, IServer>` options-pipeline registration is LAZY — the
        // delegate fires the first time `IOptionsMonitor<OpenIddictServerOptions>.CurrentValue` is
        // materialized, which for the server options happens inside the per-request
        // `OpenIddictServerDispatcher`. By that point Kestrel has finished binding and
        // IServerAddressesFeature.Addresses is populated. Eager resolution via a synchronous
        // `IConfiguration` read at Startup was the old shape; it would miss late-bound sources
        // (WebApplicationFactory in-memory overrides) and it cannot read IServer (which exists in
        // DI but has no addresses until StartAsync completes).
        services.AddOptions<OpenIddictServerOptions>()
            .Configure<IConfiguration, IServer>((opt, cfg, server) =>
            {
                var issuer = ResolveIssuer(cfg, server);
                if (issuer is not null)
                {
                    opt.Issuer = issuer;
                }
            });
        // UseLocalServer imports Issuer from server options via IOptionsMonitor<OpenIddictServerOptions>
        // at validation-options materialization time, so pinning the server side is normally enough.
        // We still pin validation explicitly as a belt-and-braces defense in case the validation
        // stack is ever reconfigured off UseLocalServer (e.g., introspection).
        services.AddOptions<OpenIddictValidationOptions>()
            .Configure<IConfiguration, IServer>((opt, cfg, server) =>
            {
                var issuer = ResolveIssuer(cfg, server);
                if (issuer is not null)
                {
                    opt.Issuer = issuer;
                }
            });

        services.AddOpenIddict()
            .AddCore(o => o
                .UseEntityFrameworkCore()
                .UseDbContext<StrgDbContext>())
            .AddServer(o =>
            {
                o.SetTokenEndpointUris("/connect/token");
                o.SetAuthorizationEndpointUris("/connect/authorize");
                o.SetUserInfoEndpointUris("/connect/userinfo");
                o.SetIntrospectionEndpointUris("/connect/introspect");
                o.SetRevocationEndpointUris("/connect/revoke");

                o.AllowPasswordFlow();
                o.AllowAuthorizationCodeFlow();
                o.AllowRefreshTokenFlow();

                // PKCE for the authorization-code flow. Mitigates authorization-code interception
                // and is required for public clients (SPAs, mobile) that cannot protect a client
                // secret. Password flow is unaffected — PKCE only binds to code exchange.
                o.RequireProofKeyForCodeExchange();

                o.RegisterScopes(
                    "files.read", "files.write", "files.share",
                    "tags.write", "admin",
                    OpenIddictConstants.Scopes.OpenId,
                    OpenIddictConstants.Scopes.Profile,
                    OpenIddictConstants.Scopes.Email,
                    // Required for refresh-token issuance: OpenIddict 7.x only emits a refresh
                    // token when both the request and the client permission set include this
                    // scope. Without it, RefreshToken grant-type is dead code.
                    OpenIddictConstants.Scopes.OfflineAccess);

                o.SetAccessTokenLifetime(TimeSpan.FromMinutes(15));
                // Sliding expiration is enabled by default; SetRefreshTokenLifetime sets the rolling window
                o.SetRefreshTokenLifetime(TimeSpan.FromDays(30));
                o.SetRefreshTokenReuseLeeway(TimeSpan.FromSeconds(30));

                // Disable access token encryption so JWTs are readable by third parties
                o.DisableAccessTokenEncryption();

                if (isDevelopment)
                {
                    o.AddEphemeralEncryptionKey();
                    o.AddEphemeralSigningKey();
                }
                else
                {
                    // Production requires persistent keys. Ephemeral keys are per-process and
                    // would invalidate every issued token on replica restart, and replicas would
                    // reject each other's tokens — fatal across a rolling deploy. Fail fast on
                    // missing config rather than silently booting with an auto-generated key.
                    o.AddSigningCertificate(LoadCertificate(config, SigningCertKeyPrefix));
                    o.AddEncryptionCertificate(LoadCertificate(config, EncryptionCertKeyPrefix));
                }

                o.UseAspNetCore()
                 .EnableTokenEndpointPassthrough()
                 .EnableAuthorizationEndpointPassthrough()
                 .EnableUserInfoEndpointPassthrough();
            })
            .AddValidation(o =>
            {
                o.UseLocalServer();
                o.UseAspNetCore();
            });

        return services;
    }

    // Resolves the Issuer URI for both server and validation options. Order: explicit config-key
    // override wins (operators behind a reverse proxy, or any scenario where the external-facing
    // issuer differs from the Kestrel binding) — otherwise self-detect from IServerAddressesFeature
    // via ServerAddressNormalizer. Returns null if neither source is available, in which case
    // OpenIddict falls back to the per-request BaseUri (the pre-STRG-074 buggy behavior, which we
    // leave in place for the narrow case where a host runs WITHOUT Kestrel — e.g., a TestServer
    // harness where IServerAddressesFeature legitimately has no addresses).
    private static Uri? ResolveIssuer(IConfiguration config, IServer server)
    {
        var configured = config["OpenIddict:Issuer"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new Uri(configured, UriKind.Absolute);
        }

        var normalized = ServerAddressNormalizer.TryResolve(server);
        return normalized is null ? null : new Uri(normalized, UriKind.Absolute);
    }

    // Loads a PKCS#12 (.pfx/.p12) bundle from disk. File path + optional password is the
    // canonical Kubernetes Secret / Docker secret delivery pattern: mount the bundle into the
    // container and point the config key at the mount path. PEM-in-env-var was considered and
    // rejected — newline escaping and ~4 KB env-var limits make it fragile for production keys.
    //
    // RS1035 flags File.* and X509CertificateLoader project-wide (EnforceExtendedAnalyzerRules
    // in Directory.Build.props) — that rule targets Roslyn analyzer assemblies; this is runtime
    // host startup code.
#pragma warning disable RS1035
    private static X509Certificate2 LoadCertificate(IConfiguration config, string keyPrefix)
    {
        var pathKey = $"OpenIddict:{keyPrefix}Path";
        var path = config[pathKey];
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                $"{pathKey} is not configured. Non-Development environments require a persistent " +
                $"X.509 certificate (PKCS#12 .pfx/.p12 file with private key). Set {pathKey} via " +
                $"appsettings or the OpenIddict__{keyPrefix}Path environment variable.");
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"{pathKey} points to '{path}', which does not exist or is not readable by the " +
                $"current process. Verify the file path and container mount permissions.");
        }

        var password = config[$"OpenIddict:{keyPrefix}Password"];

        try
        {
            // .NET 9+ API — replaces the `new X509Certificate2(path, password)` constructor,
            // which is obsolete under SYSLIB0057.
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password);
            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new InvalidOperationException(
                    $"Certificate at '{path}' does not contain a private key. OpenIddict requires " +
                    $"a PKCS#12 bundle that includes the private key for signing/encryption.");
            }

            return certificate;
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"Failed to load OpenIddict certificate from '{path}'. Verify the file is a valid " +
                $"PKCS#12 bundle and that OpenIddict:{keyPrefix}Password matches the file password.",
                ex);
        }
    }
#pragma warning restore RS1035
}
