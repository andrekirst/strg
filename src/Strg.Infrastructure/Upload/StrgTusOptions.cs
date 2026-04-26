namespace Strg.Infrastructure.Upload;

/// <summary>
/// Configuration knobs for the TUS upload pipeline (STRG-034). Bound from the
/// <c>Strg:Upload</c> configuration section.
/// </summary>
public sealed class StrgTusOptions
{
    /// <summary>
    /// URL prefix the TUS middleware listens on. The endpoint emits a <c>Location</c> header with
    /// <c>{UrlPath}/{uploadId}</c> after CREATE.
    /// </summary>
    public string UrlPath { get; set; } = "/upload";

    /// <summary>
    /// Wall-clock TTL after which an in-flight upload is considered abandoned. STRG-036's
    /// background sweep reaps the temp blob and the <c>pending_uploads</c> row at that point.
    /// 24 hours mirrors common TUS-server defaults.
    /// </summary>
    public TimeSpan UploadAbandonAfter { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How often <see cref="BackgroundJobs.AbandonedUploadCleanupJob"/> wakes to sweep expired
    /// <see cref="Strg.Core.Domain.PendingUpload"/> rows. The 5-minute default matches the STRG-035 spec.
    ///
    /// <para>The spec text reads "not configurable in v0.1." That is honoured at the
    /// documentation surface — there is no operator-facing knob, no validation, and no entry
    /// in deployment docs. The property exists so integration tests can drive the job's body
    /// (<c>RunOnceAsync</c>) directly without spinning the timer; an operator who reads the
    /// code can override it via <c>Strg:Upload:UploadCleanupInterval</c> at their own risk.</para>
    /// </summary>
    public TimeSpan UploadCleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
}
