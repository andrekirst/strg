using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Net.Http.Headers;

namespace Strg.Api.HealthChecks;

/// <summary>
/// Response writer for the unauthenticated <c>/health/ready</c> endpoint (STRG-008).
///
/// <para><b>Why this exists instead of <c>UIResponseWriter.WriteHealthCheckUIResponse</c>:</b>
/// the Xabaril writer serializes <see cref="HealthReportEntry.Exception"/>'s message into the
/// JSON body, and the Npgsql <c>NpgsqlException</c> raised by <c>AddDbContextCheck</c> when the
/// configured database is unreachable typically embeds the connection string's host (e.g.
/// <c>"Failed to connect to db.internal.example.com:5432"</c>) and, on auth failure, the username.
/// Returning that JSON to an unauthenticated probe would violate the STRG-008 security checklist
/// item <i>"Response does not reveal database connection string details"</i>.</para>
///
/// <para>Wire format is intentionally minimal: <c>{ "status", "checks": [ { "name", "status",
/// "duration_ms", "description"? } ] }</c>. <see cref="HealthReportEntry.Exception"/> is never
/// serialized; <see cref="HealthReportEntry.Data"/> is not serialized either — any future
/// contributor adding <c>data["root_path"]</c> or similar would silently leak it; opting out
/// by default is safer than auditing every contribution. <see cref="HealthReportEntry.Description"/>
/// is included only when the underlying check returned a non-empty description AND no exception
/// was captured: when an <see cref="IHealthCheck"/> throws (instead of returning a
/// <see cref="HealthCheckResult"/>), the framework constructs the entry with
/// <c>description = ex.Message</c>, and Npgsql exception messages embed the connection-string
/// host. Suppressing description in that case turns the description-leak channel from "safe by
/// convention (every check author catches their own exceptions)" into "safe by construction".</para>
/// </summary>
internal static class SafeHealthCheckResponseWriter
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    public static async Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers[HeaderNames.CacheControl] = "no-store, no-cache";

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("status", report.Status.ToString());
            writer.WriteNumber("total_duration_ms", (long)report.TotalDuration.TotalMilliseconds);

            writer.WriteStartArray("checks");
            foreach (var entry in report.Entries)
            {
                writer.WriteStartObject();
                writer.WriteString("name", entry.Key);
                writer.WriteString("status", entry.Value.Status.ToString());
                writer.WriteNumber("duration_ms", (long)entry.Value.Duration.TotalMilliseconds);
                // Suppress description when an exception was captured: the framework's default
                // path on a throwing check is `description = ex.Message`, which for Npgsql
                // contains the database host. Caller-supplied descriptions on non-throwing
                // checks remain visible (e.g. StorageHealthCheck's "no default local drive
                // provisioned").
                if (entry.Value.Exception is null && !string.IsNullOrEmpty(entry.Value.Description))
                {
                    writer.WriteString("description", entry.Value.Description);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        await context.Response.Body.WriteAsync(buffer.ToArray(), context.RequestAborted);
    }
}
