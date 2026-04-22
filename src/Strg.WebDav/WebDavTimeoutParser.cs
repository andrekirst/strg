using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace Strg.WebDav;

/// <summary>
/// STRG-072 — parses the RFC 4918 §10.7 <c>Timeout</c> header. Shape is a comma-separated list of
/// <c>Second-{n}</c> or <c>Infinite</c>; clients send multiple entries in preference order. We take
/// the first value we can parse and clamp to <paramref name="maxSeconds"/>, never honoring
/// <c>Infinite</c> verbatim — an unbounded lock on a shared resource is a denial-of-service on
/// every other user who needs to write to the same file.
///
/// <para>A missing or unparseable header falls back to <paramref name="defaultSeconds"/>. Negative
/// or zero values are treated as "use default" rather than "fail the request" because RFC 4918
/// doesn't define an error for bad Timeout formatting and refusing a LOCK over a malformed
/// preference would be more user-hostile than silently picking a sane duration.</para>
/// </summary>
internal static class WebDavTimeoutParser
{
    public static TimeSpan Parse(StringValues header, int defaultSeconds, int maxSeconds)
    {
        if (StringValues.IsNullOrEmpty(header))
        {
            return TimeSpan.FromSeconds(defaultSeconds);
        }

        // Header may be delivered as a single comma-separated string OR as multiple StringValues
        // entries; the comma-split handles both. We iterate until we find a parseable entry.
        foreach (var raw in header)
        {
            if (raw is null)
            {
                continue;
            }

            foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("Second-", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(part.AsSpan("Second-".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                    && seconds > 0)
                {
                    // Clamp: never grant more than the server-configured ceiling regardless of
                    // what the client asked for. Returning exactly maxSeconds (via Math.Min) is
                    // RFC 4918 §10.7-compliant — the server controls the outcome.
                    return TimeSpan.FromSeconds(Math.Min(seconds, maxSeconds));
                }

                // "Infinite" → clamp to ceiling. Never honor verbatim; see class doc.
                if (string.Equals(part, "Infinite", StringComparison.OrdinalIgnoreCase))
                {
                    return TimeSpan.FromSeconds(maxSeconds);
                }
            }
        }

        return TimeSpan.FromSeconds(defaultSeconds);
    }
}
