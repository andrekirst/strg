using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
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
                    OpenIddictConstants.Scopes.Email);

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
