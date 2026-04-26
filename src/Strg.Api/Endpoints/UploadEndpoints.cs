using Strg.Infrastructure.Upload;
using tusdotnet;
using tusdotnet.Models;

namespace Strg.Api.Endpoints;

/// <summary>
/// Registers the TUS upload endpoint (STRG-034) using endpoint routing — <c>MapTus</c> gives us
/// <c>.RequireAuthorization()</c> and <c>.DisableRateLimiting()</c> chaining, which the older
/// middleware-based <c>app.UseTus(...)</c> would not.
///
/// <para><b>Rate limiting.</b> Per Phase-2 design memory ("TUS after auth + excluded from rate
/// limiting"): the chunked-byte volume of a TUS upload would saturate the per-IP fixed-window
/// limiter immediately. Operators control upload throughput via Kestrel limits and the per-user
/// quota; the global rate limiter is for request-rate abuse, not byte-rate.</para>
///
/// <para><b>Authorization.</b> <c>.RequireAuthorization()</c> is explicit even though the global
/// <c>FallbackPolicy</c> would also enforce it — being explicit is defence-in-depth and matches
/// the existing endpoint-mapping pattern. The TUS <c>OnAuthorizeAsync</c> hook (in
/// <see cref="StrgTusEvents"/>) provides a second, intent-aware check.</para>
/// </summary>
public static class UploadEndpoints
{
    /// <summary>
    /// Single Events instance — captured once at registration time. The static handler delegates
    /// inside <see cref="StrgTusEvents"/> read all per-request state from the
    /// <c>AuthorizeContext</c> / <c>BeforeCreateContext</c> / <c>FileCompleteContext</c> arguments,
    /// so a single shared instance is correct (and avoids per-request allocation).
    /// </summary>
    private static readonly tusdotnet.Models.Configuration.Events SharedEvents = StrgTusEvents.Build();

    public static IEndpointRouteBuilder MapStrgTusUpload(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapTus("/upload", httpContext => Task.FromResult(new DefaultTusConfiguration
        {
            Store = httpContext.RequestServices.GetRequiredService<StrgTusStore>(),
            Events = SharedEvents,

            // AllowEmptyValues: TUS clients differ on whether a metadata key with no value is
            // legal. The default `AllowEmptyValues` keeps STRG-034 client-compatible; OnBeforeCreate
            // explicitly rejects empty path/filename anyway.
            MetadataParsingStrategy = MetadataParsingStrategy.AllowEmptyValues,

            // No hardcoded MaxAllowedUploadSize — quota is the limit, per AC. Operators that want
            // a hard cap below quota set it via Kestrel limits.
            MaxAllowedUploadSizeInBytesLong = null,
        }))
        .RequireAuthorization()
        .DisableRateLimiting()
        .WithName("StrgTusUpload");

        return endpoints;
    }
}
