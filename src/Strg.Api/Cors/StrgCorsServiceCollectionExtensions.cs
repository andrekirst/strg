namespace Strg.Api.Cors;

/// <summary>
/// CORS wiring for STRG-010. Registers a single named policy <see cref="PolicyName"/> bound
/// from <c>Cors:AllowedOrigins</c>, with a startup guard that rejects wildcard origins.
///
/// <para>
/// The wildcard guard exists because the policy calls <c>AllowCredentials</c>, and the CORS
/// specification forbids <c>Access-Control-Allow-Origin: *</c> together with credentials. Without this guard a wildcard slips through to request time where the CORS
/// middleware silently omits the <c>Access-Control-Allow-Origin</c> header, and the failure
/// manifests only as a browser-side "blocked by CORS policy" error — a much harder signal to
/// trace back to misconfiguration than a fail-fast startup exception.
/// </para>
/// </summary>
internal static class StrgCorsServiceCollectionExtensions
{
    /// <summary>Name of the CORS policy registered by <see cref="AddStrgCors"/>.</summary>
    public const string PolicyName = "strg-cors";

    /// <summary>
    /// Configuration key (<c>Cors:AllowedOrigins</c>) bound into a <see cref="string"/> array
    /// of exact-match origins. Callers may not include <c>*</c> in any entry; see the type
    /// remarks for the rationale.
    /// </summary>
    public const string AllowedOriginsConfigurationKey = "Cors:AllowedOrigins";

    public static IServiceCollection AddStrgCors(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var origins = configuration.GetSection(AllowedOriginsConfigurationKey).Get<string[]>() ?? [];

        foreach (var origin in origins)
        {
            if (origin.Contains('*', StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"STRG-010: wildcard origin '{origin}' is forbidden in "
                    + $"'{AllowedOriginsConfigurationKey}' because policy '{PolicyName}' uses "
                    + "AllowCredentials(). List explicit origins or remove the wildcard entry.");
            }
        }

        services.AddCors(options =>
        {
            options.AddPolicy(PolicyName, policy =>
            {
                policy.WithOrigins(origins)
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials();
            });
        });

        return services;
    }
}
