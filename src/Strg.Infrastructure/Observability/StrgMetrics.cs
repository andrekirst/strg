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
    }

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
