namespace Strg.WebDav;

/// <summary>
/// STRG-072 — the RFC 4918 §10.4 <c>If:</c> header can be elaborate (tagged lists, ETags, lock
/// tokens wrapped in <c>&lt;...&gt;</c>). v0.1 only needs the subset that lets a client write
/// through their own lock: one or more lock-token references. We pluck the first URI out of
/// <c>&lt;...&gt;</c> brackets and ignore the rest — a lock token match is all the write-gate
/// needs.
///
/// <para>A client presenting a fancy compound If-header (e.g. <c>(&lt;token-A&gt; [etag-B])</c>)
/// still succeeds because we only extract the first lock token; the extra conditions are advisory
/// from the client's perspective. A malformed header (no bracket pair) returns <c>null</c> —
/// <see cref="IStrgWebDavLockManager.CanWriteAsync"/> treats that the same as "no token
/// presented" and emits 423 if the resource is locked.</para>
/// </summary>
internal static class WebDavIfHeader
{
    public static string? ExtractFirstLockToken(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var start = header.IndexOf('<', StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var end = header.IndexOf('>', start + 1);
        if (end <= start + 1)
        {
            return null;
        }

        // Token shape is urn:uuid:{hex}. We don't validate the prefix here — lock manager compares
        // by equality, so a bogus string just fails the write-gate cleanly.
        return header[(start + 1)..end];
    }

    /// <summary>
    /// Extracts the token from the <c>Lock-Token</c> request header (RFC 4918 §10.5). Shape is
    /// <c>&lt;urn:uuid:...&gt;</c> for UNLOCK — same extraction logic but exposed under the name
    /// that matches the header's purpose.
    /// </summary>
    public static string? ExtractLockTokenHeader(string? header) => ExtractFirstLockToken(header);
}
