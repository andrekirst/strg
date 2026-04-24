namespace Strg.Api.RateLimiting;

/// <summary>
/// Configuration binding for STRG-010's rate-limiter. Read from the <c>RateLimiting</c> root in
/// appsettings. v0.1 uses in-process fixed-window partitions; v0.3 migrates to Redis for
/// multi-node deployments (STRG-117).
/// </summary>
internal sealed class RateLimitOptions
{
    /// <summary>Configuration root key — <c>services.Configure&lt;RateLimitOptions&gt;(config.GetSection(SectionName))</c>.</summary>
    public const string SectionName = "RateLimiting";

    /// <summary>Per-IP budget for the <see cref="RateLimitPolicies.Auth"/> named policy.</summary>
    public RateLimitPolicyOptions Auth { get; set; } = new() { PermitLimit = 10, WindowSeconds = 60 };

    /// <summary>
    /// Per-IP budget enforced on every request via the <c>GlobalLimiter</c>. Applies in addition
    /// to any endpoint-specific named policy — a token-endpoint request consumes both Auth and
    /// Global budgets for its IP partition.
    /// </summary>
    public RateLimitPolicyOptions Global { get; set; } = new() { PermitLimit = 1000, WindowSeconds = 60 };
}

/// <summary>
/// One partition's worth of fixed-window parameters. Defaults match STRG-082's v0.1 numbers;
/// every property is intentionally settable so tests can dial the window down for deterministic
/// 429 pins.
/// </summary>
internal sealed class RateLimitPolicyOptions
{
    /// <summary>Requests permitted per window per partition (per IP).</summary>
    public int PermitLimit { get; set; } = 100;

    /// <summary>Fixed-window length in seconds.</summary>
    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// Queue depth. Zero (the default) makes the limiter reject overruns immediately; positive
    /// values would buffer requests briefly. Keep at zero for HTTP — a queued request already
    /// holds a connection, so queueing is a DoS amplifier rather than a smoothing technique.
    /// </summary>
    public int QueueLimit { get; set; }
}
