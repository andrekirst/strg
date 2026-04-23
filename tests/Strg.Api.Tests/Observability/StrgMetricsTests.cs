using System.Diagnostics.Metrics;
using FluentAssertions;
using Strg.Infrastructure.Observability;
using Xunit;

namespace Strg.Api.Tests.Observability;

/// <summary>
/// TC-003 — Pure unit tests for <see cref="StrgMetrics"/>. Uses <see cref="MeterListener"/>
/// to observe measurements without a full OTel pipeline.
///
/// IMPORTANT: The listener MUST be started before the <see cref="Meter"/> is created because
/// <see cref="MeterListener.InstrumentPublished"/> fires only for instruments created after
/// <see cref="MeterListener.Start"/> is called. Starting the listener first ensures
/// <c>EnableMeasurementEvents</c> is called before any <c>Add</c> is invoked.
/// </summary>
public sealed class StrgMetricsTests
{
    // TC-003a: IncrementUploads(bytes) records strg_uploads_total=1 and strg_upload_bytes_total=bytes.
    [Fact]
    public void IncrementUploads_records_uploads_total_1_and_upload_bytes_total()
    {
        var uploadMeasurements = new List<long>();
        var bytesMeasurements = new List<long>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name != StrgMetrics.MeterName)
            {
                return;
            }

            if (instrument.Name == "strg_uploads_total" || instrument.Name == "strg_upload_bytes_total")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "strg_uploads_total")
            {
                uploadMeasurements.Add(measurement);
            }
            else if (instrument.Name == "strg_upload_bytes_total")
            {
                bytesMeasurements.Add(measurement);
            }
        });

        // Start listener BEFORE creating StrgMetrics so InstrumentPublished fires for its counters.
        listener.Start();

        using var metrics = new StrgMetrics();
        metrics.IncrementUploads(bytes: 42);

        uploadMeasurements.Should().ContainSingle()
            .Which.Should().Be(1, "each call to IncrementUploads must add 1 to strg_uploads_total");
        bytesMeasurements.Should().ContainSingle()
            .Which.Should().Be(42, "IncrementUploads(42) must add 42 to strg_upload_bytes_total");
    }

    // TC-003b: IncrementDownloads() records strg_downloads_total=1.
    [Fact]
    public void IncrementDownloads_records_downloads_total_1()
    {
        var downloadMeasurements = new List<long>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == StrgMetrics.MeterName
                && instrument.Name == "strg_downloads_total")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "strg_downloads_total")
            {
                downloadMeasurements.Add(measurement);
            }
        });

        listener.Start();

        using var metrics = new StrgMetrics();
        metrics.IncrementDownloads();

        downloadMeasurements.Should().ContainSingle()
            .Which.Should().Be(1, "each call to IncrementDownloads must add 1 to strg_downloads_total");
    }

    // TC-003c: AddConnection() records +1, RemoveConnection() records -1 on strg_active_connections.
    [Fact]
    public void AddConnection_records_plus1_and_RemoveConnection_records_minus1()
    {
        var connectionMeasurements = new List<long>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == StrgMetrics.MeterName
                && instrument.Name == "strg_active_connections")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "strg_active_connections")
            {
                connectionMeasurements.Add(measurement);
            }
        });

        listener.Start();

        using var metrics = new StrgMetrics();
        metrics.AddConnection();
        metrics.RemoveConnection();

        connectionMeasurements.Should().HaveCount(2, "AddConnection then RemoveConnection produce two measurements");
        connectionMeasurements[0].Should().Be(1, "AddConnection must record +1");
        connectionMeasurements[1].Should().Be(-1, "RemoveConnection must record -1");
    }
}
