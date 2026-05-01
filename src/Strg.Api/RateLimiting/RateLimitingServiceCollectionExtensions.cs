using System.Globalization;
using System.Net.Mime;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Strg.Core.Constants;

namespace Strg.Api.RateLimiting;

/// <summary>
/// Rate-limiter wiring for STRG-010 / STRG-082. Registers:
/// <list type="bullet">
///   <item><description>A <c>GlobalLimiter</c> keyed on remote IP — applies to every request
///   that does not chain <c>DisableRateLimiting()</c> on its endpoint mapping.</description></item>
///   <item><description>The <see cref="RateLimitPolicies.Auth"/> named policy — attached at
///   the endpoint via <c>RequireRateLimiting</c> on <c>/connect/token</c>.</description></item>
///   <item><description>The <see cref="RateLimitPolicies.Upload"/> named policy — partitioned
///   on the JWT subject. Defined for future chunked-upload endpoints; v0.1 leaves TUS exempted
///   per the STRG-082 spec.</description></item>
/// </list>
///
/// <para>
/// All three use <see cref="FixedWindowRateLimiter"/> with an in-memory partition store.
/// Multi-node deployments need a shared store; STRG-117 tracks the Redis migration and the
/// in-memory store is an explicit v0.1 limitation. Rejected requests return 429 Too Many
/// Requests with a <c>Retry-After</c> header and a JSON error body.
/// </para>
/// </summary>
internal static class RateLimitingServiceCollectionExtensions
{
    public static IServiceCollection AddStrgRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.SectionName));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = OnRejectedAsync;

            limiter.AddPolicy(RateLimitPolicies.Auth, context =>
            {
                var policyOptions = context.RequestServices
                    .GetRequiredService<IOptionsMonitor<RateLimitOptions>>()
                    .CurrentValue.Auth;
                return BuildFixedWindowPartition(context, policyOptions);
            });

            limiter.AddPolicy(RateLimitPolicies.Upload, context =>
            {
                var policyOptions = context.RequestServices
                    .GetRequiredService<IOptionsMonitor<RateLimitOptions>>()
                    .CurrentValue.Upload;
                return BuildUploadPartition(context, policyOptions);
            });

            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var policyOptions = context.RequestServices
                    .GetRequiredService<IOptionsMonitor<RateLimitOptions>>()
                    .CurrentValue.Global;
                return BuildFixedWindowPartition(context, policyOptions);
            });
        });

        return services;
    }

    // Rejection envelope: a Retry-After hint plus a minimal JSON body matching the {"error": ...}
    // shape the rest of the API uses. Retry-After is sourced from the lease's RetryAfter
    // metadata (FixedWindowRateLimiter populates this with the time to the next window reset),
    // with a 60-second fallback for limiters that don't surface the metadata.
    //
    // TODO: v0.2 — replace the in-memory partition store with a Redis-backed shared counter so
    // multi-instance deployments share budgets (STRG-117). The named policies and the
    // partition-key strategy stay; only the storage substrate changes.
    private static ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var retryAfterSeconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : 60;

        var response = context.HttpContext.Response;
        response.Headers[HeaderNames.RetryAfter] = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        response.ContentType = MediaTypeNames.Application.Json;

        var body = $"{{\"error\":\"Rate limit exceeded. Try again in {retryAfterSeconds} seconds.\"}}";
        return new ValueTask(response.WriteAsync(body, cancellationToken));
    }

    private static RateLimitPartition<string> BuildFixedWindowPartition(
        HttpContext context,
        RateLimitPolicyOptions options)
    {
        var partitionKey = ResolvePartitionKey(context);
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.PermitLimit,
                Window = TimeSpan.FromSeconds(options.WindowSeconds),
                QueueLimit = options.QueueLimit,
            });
    }

    // Upload partition: prefer the JWT subject so authenticated users carry a per-identity
    // budget that survives reverse-proxy IP coalescing. Fall back to the IP when no JWT is
    // present (the policy is registered for future opt-in routes; keeping anonymous traffic
    // bucketed by IP avoids one shared "anon" partition becoming the default DoS amplifier).
    private static RateLimitPartition<string> BuildUploadPartition(
        HttpContext context,
        RateLimitPolicyOptions options)
    {
        var subject = context.User.FindFirst(StrgClaimNames.Subject)?.Value;
        var partitionKey = !string.IsNullOrEmpty(subject) ? subject : ResolvePartitionKey(context);
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.PermitLimit,
                Window = TimeSpan.FromSeconds(options.WindowSeconds),
                QueueLimit = options.QueueLimit,
            });
    }

    // Partition by remote IP, honouring the X-Forwarded-For convention used elsewhere in the
    // codebase (TokenEndpoints.GetClientIp). Reverse-proxied deployments rewrite the socket
    // peer address, so keying solely on Connection.RemoteIpAddress would lump every real
    // client into the proxy's single partition. "unknown" is the fallback sentinel — all
    // requests without a resolvable address share one partition rather than creating an
    // unbounded number of zero-keyed ones.
    private static string ResolvePartitionKey(HttpContext context)
    {
        var forwardedFor = context.Request.Headers[StrgHeaderNames.XForwardedFor].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            var first = forwardedFor.Split(',', 2)[0].Trim();
            if (first.Length > 0)
            {
                return first;
            }
        }
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
