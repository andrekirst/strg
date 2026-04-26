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
}
