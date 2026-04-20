using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Strg.Infrastructure.Data;

namespace Strg.Infrastructure.Identity;

public static class OpenIddictConfiguration
{
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
}
