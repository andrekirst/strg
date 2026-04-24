using System.Diagnostics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Strg.Infrastructure.Data;
using Strg.Integration.Tests.Auth;
using Xunit;

namespace Strg.Integration.Tests.Observability;

/// <summary>
/// TC-001 — A real HTTP request through the ASP.NET Core pipeline produces at least one
/// OpenTelemetry trace span. Validates that <see cref="ObservabilityServiceCollectionExtensions"/>
/// correctly wires AspNetCore instrumentation and that the in-memory exporter can observe spans.
/// </summary>
public sealed class TracingTests(StrgWebApplicationFactory factory) : IClassFixture<StrgWebApplicationFactory>
{
    // TC-001: HTTP request to a non-noise endpoint produces a trace span with status_code tag.
    // /nonexistent is deliberately not in IsNoiseEndpoint (/health, /healthz, /metrics) and
    // the fallback policy returns 404 from routing — a trace span is still emitted.
    [Fact]
    public async Task Http_request_produces_trace_span_with_status_code_tag()
    {
        var exportedActivities = new List<Activity>();

        // Stack an in-memory exporter alongside the OTLP exporter that AddStrgObservability
        // already registered. ConfigureOpenTelemetryTracerProvider merges into the existing
        // builder — we do NOT rebuild or replace the provider.
        await using var tracerFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.ConfigureOpenTelemetryTracerProvider(tracing =>
                    tracing.AddInMemoryExporter(exportedActivities));
            });
        });

        using var client = tracerFactory.CreateClient();

        using var response = await client.GetAsync("/nonexistent-strg-trace-test-path");

        // Force-flush so the in-memory exporter has received all pending spans before we assert.
        var tracerProvider = tracerFactory.Services.GetRequiredService<TracerProvider>();
        tracerProvider.ForceFlush(timeoutMilliseconds: 5000);

        // A 404 from routing still produces a span. The span DisplayName for ASP.NET Core
        // instrumentation uses the HTTP method + route template; for unmatched routes it is
        // typically "GET /nonexistent-strg-trace-test-path" or just "GET".
        exportedActivities.Should().NotBeEmpty("at least one span must be exported for a real HTTP request");

        // `Contain` rather than `ContainSingle`: future instrumentation (or EF Core from a
        // middleware-issued query) may produce additional status-code-tagged spans in the same
        // scope. The AC only requires that at least one such span exists.
        exportedActivities.Should().Contain(
            a => a.TagObjects.Any(t => t.Key.Contains("status_code", StringComparison.OrdinalIgnoreCase)),
            "the trace must carry at least one span with an HTTP status code tag");

        exportedActivities.Should().Contain(
            a => a.DisplayName.Contains("GET", StringComparison.OrdinalIgnoreCase),
            "AspNetCore instrumentation names server spans with the HTTP method (e.g. 'GET /path')");
    }

    // TC-001b (AC-3): EF Core queries produce child spans alongside the HTTP server span.
    // Runs an EF Core query directly through the factory's DI scope so the assertion is
    // independent of any specific HTTP endpoint's query behavior.
    [Fact]
    public async Task EfCore_query_produces_span_on_Strg_meter_pipeline()
    {
        var exportedActivities = new List<Activity>();

        await using var tracerFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.ConfigureOpenTelemetryTracerProvider(tracing =>
                    tracing.AddInMemoryExporter(exportedActivities));
            });
        });

        // Boot the host so the TracerProvider is built.
        _ = tracerFactory.CreateClient();

        using (var scope = tracerFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            // Any real EF query against the live provider triggers EntityFrameworkCore
            // instrumentation. CountAsync issues a single SQL COUNT round-trip.
            _ = await db.Users.IgnoreQueryFilters().CountAsync();
        }

        var tracerProvider = tracerFactory.Services.GetRequiredService<TracerProvider>();
        tracerProvider.ForceFlush(timeoutMilliseconds: 5000);

        // EF Core instrumentation creates activities on the
        // "OpenTelemetry.Instrumentation.EntityFrameworkCore" ActivitySource.
        exportedActivities.Should().Contain(
            a => a.Source.Name.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)
                 || a.TagObjects.Any(t => t.Key.Equals("db.system", StringComparison.OrdinalIgnoreCase)
                                          || t.Key.Equals("db.statement", StringComparison.OrdinalIgnoreCase)),
            "at least one span must originate from EF Core instrumentation (source name or db.* tag)");
    }
}
