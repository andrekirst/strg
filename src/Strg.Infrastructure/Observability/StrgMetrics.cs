using System.Diagnostics.Metrics;

namespace Strg.Infrastructure.Observability;

/// <summary>
/// Owns the application-level <see cref="Meter"/> and exposes strongly-typed counters for
/// upload, download, and connection tracking. Call sites (STRG-034/037) increment these after
/// their respective operations complete successfully.
/// </summary>
/// <remarks>
/// SECURITY — high-cardinality / PII tags are PROHIBITED on every instrument in this class and
/// on any instrument added to the <c>"Strg"</c> meter. Do NOT pass <c>KeyValuePair</c> tag arrays
/// to <c>.Add(...)</c> that reference: user IDs, tenant IDs, email addresses, file names, file
/// paths, drive IDs, IP addresses, or any other user-controlled or user-identifying value. The
/// <c>/metrics</c> scrape endpoint is unauthenticated (Prometheus pull); anything emitted here
/// is visible to every scraper and persists in metric TSDBs indefinitely. Per-user metrics
/// belong in audit logs, not counters.
/// </remarks>
public sealed class StrgMetrics : IDisposable
{
    /// <summary>Meter name used by OTel registration to subscribe to this meter's instruments.</summary>
    public const string MeterName = "Strg";

    private readonly Meter _meter;

    /// <summary>Counts successful file uploads (one increment per upload).</summary>
    public Counter<long> UploadsTotal { get; }

    /// <summary>Accumulates total bytes transferred on successful uploads.</summary>
    public Counter<long> UploadBytesTotal { get; }

    /// <summary>Counts successful file downloads (one increment per download).</summary>
    public Counter<long> DownloadsTotal { get; }

    /// <summary>Tracks currently active WebDAV/WebSocket connections.</summary>
    public UpDownCounter<long> ActiveConnections { get; }

    /// <summary>Counts thumbnail generation outcomes — labelled <c>format</c> / <c>variant</c> / <c>status</c>.</summary>
    public Counter<long> ThumbnailsGeneratedTotal { get; }

    /// <summary>Counts skipped generations — labelled <c>reason</c> (encrypted-drive, too-large, pixel-cap, unknown-mime, no-generator).</summary>
    public Counter<long> ThumbnailsSkippedTotal { get; }

    /// <summary>Histogram of generation wall time in seconds, labelled <c>format</c>.</summary>
    public Histogram<double> ThumbnailsGenerationDurationSeconds { get; }

    /// <summary>Concurrent thumbnail generations in flight (UpDownCounter, no labels).</summary>
    public UpDownCounter<long> ThumbnailsInflight { get; }

    public StrgMetrics()
    {
        _meter = new Meter(MeterName);
        UploadsTotal = _meter.CreateCounter<long>(
            "strg_uploads_total",
            unit: null,
            description: "Successful uploads");
        UploadBytesTotal = _meter.CreateCounter<long>(
            "strg_upload_bytes_total",
            unit: "By");
        DownloadsTotal = _meter.CreateCounter<long>(
            "strg_downloads_total");
        ActiveConnections = _meter.CreateUpDownCounter<long>(
            "strg_active_connections",
            description: "Active WebDAV/WebSocket connections");

        ThumbnailsGeneratedTotal = _meter.CreateCounter<long>(
            "strg_thumbnails_generated_total",
            unit: null,
            description: "Thumbnail generation outcomes per format / variant / status");
        ThumbnailsSkippedTotal = _meter.CreateCounter<long>(
            "strg_thumbnails_skipped_total",
            unit: null,
            description: "Thumbnail generation skipped (encrypted-drive, too-large, pixel-cap, unknown-mime, no-generator)");
        ThumbnailsGenerationDurationSeconds = _meter.CreateHistogram<double>(
            "strg_thumbnails_generation_duration_seconds",
            unit: "s",
            description: "Thumbnail generation wall time per format");
        ThumbnailsInflight = _meter.CreateUpDownCounter<long>(
            "strg_thumbnails_inflight",
            unit: null,
            description: "Concurrent thumbnail generations in progress");
    }

    /// <summary>Records one thumbnail generation outcome. <paramref name="status"/> is <c>"ready"</c> or <c>"timed-out"</c>.</summary>
    public void IncrementThumbnailGenerated(string format, string variant, string status) =>
        ThumbnailsGeneratedTotal.Add(1,
            new KeyValuePair<string, object?>("format", format),
            new KeyValuePair<string, object?>("variant", variant),
            new KeyValuePair<string, object?>("status", status));

    /// <summary>
    /// Records one skipped generation. <paramref name="reason"/> MUST be from the bounded set
    /// <c>encrypted-drive | too-large | pixel-cap | unknown-mime | no-generator</c>.
    /// </summary>
    public void IncrementThumbnailSkipped(string reason) =>
        ThumbnailsSkippedTotal.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Records the wall time of one generation, labelled by output format.</summary>
    public void RecordThumbnailDuration(string format, double seconds) =>
        ThumbnailsGenerationDurationSeconds.Record(seconds,
            new KeyValuePair<string, object?>("format", format));

    /// <summary>Records one successful upload and the bytes transferred.</summary>
    public void IncrementUploads(long bytes)
    {
        UploadsTotal.Add(1);
        UploadBytesTotal.Add(bytes);
    }

    /// <summary>Records one successful download.</summary>
    public void IncrementDownloads()
    {
        DownloadsTotal.Add(1);
    }

    /// <summary>Increments the active-connection gauge by one.</summary>
    public void AddConnection()
    {
        ActiveConnections.Add(1);
    }

    /// <summary>Decrements the active-connection gauge by one.</summary>
    public void RemoveConnection()
    {
        ActiveConnections.Add(-1);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _meter.Dispose();
    }
}
