using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Strg.Infrastructure.Observability;

/// <summary>
/// Wires OpenTelemetry tracing + metrics and registers <see cref="StrgMetrics"/> in DI.
/// </summary>
public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="StrgMetrics"/> as a singleton and configures OpenTelemetry with
    /// ASP.NET Core / EF Core / runtime instrumentation, an OTLP trace exporter, and a
    /// Prometheus metrics scraping endpoint.
    /// </summary>
    /// <remarks>
    /// OTLP endpoint resolution order (first non-null wins):
    /// <list type="number">
    ///   <item><description>Environment variable <c>OTEL_EXPORTER_OTLP_ENDPOINT</c></description></item>
    ///   <item><description>Configuration key <c>Observability:OtlpEndpoint</c></description></item>
    ///   <item><description>Hard-coded default <c>http://localhost:4317</c></description></item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddStrgObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<StrgMetrics>();

        var otlpEndpoint =
            Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
            ?? configuration["Observability:OtlpEndpoint"]
            ?? "http://localhost:4317";

        services
            .AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("strg"))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(o =>
                {
                    o.Filter = ctx => !IsNoiseEndpoint(ctx.Request.Path);
                })
                .AddEntityFrameworkCoreInstrumentation()
                .AddSource("Strg.*")
                .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(StrgMetrics.MeterName)
                .AddPrometheusExporter());

        return services;
    }

    /// <summary>
    /// Returns <see langword="true"/> for paths that produce high-cardinality, low-value spans
    /// (health checks and metrics scraping). Pre-emptively includes <c>/health</c> and
    /// <c>/healthz</c> for STRG-008, which is not yet wired but will land on these paths.
    /// </summary>
    private static bool IsNoiseEndpoint(PathString path)
    {
        return path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/metrics", StringComparison.OrdinalIgnoreCase);
    }
}
