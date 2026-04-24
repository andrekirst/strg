using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Strg.Core.Constants;

namespace Strg.Api.RateLimiting;

/// <summary>
/// Rate-limiter wiring for STRG-010. Registers:
/// <list type="bullet">
///   <item><description>A <c>GlobalLimiter</c> keyed on remote IP — applies to every request
///   that does not chain <c>DisableRateLimiting()</c> on its endpoint mapping.</description></item>
///   <item><description>The <see cref="RateLimitPolicies.Auth"/> named policy — attached at
///   the endpoint via <c>RequireRateLimiting</c> on <c>/connect/token</c>.</description></item>
/// </list>
///
/// <para>
/// Both use <see cref="FixedWindowRateLimiter"/> with an in-memory partition store. Multi-node
/// deployments need a shared store; STRG-117 tracks the Redis migration and the in-memory
/// store is an explicit v0.1 limitation. Rejected requests return 429 Too Many Requests.
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

            limiter.AddPolicy(RateLimitPolicies.Auth, context =>
            {
                var policyOptions = context.RequestServices
                    .GetRequiredService<IOptionsMonitor<RateLimitOptions>>()
                    .CurrentValue.Auth;
                return BuildFixedWindowPartition(context, policyOptions);
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
