using System.Globalization;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Strg.WebDav;

/// <summary>
/// STRG-068 + STRG-069 PROPFIND / GET responder. Writes the RFC 4918 §14.16 multistatus XML
/// Windows Explorer and macOS Finder need to browse a drive, plus the <c>strg:</c>-namespace
/// dead properties (<c>contenthash</c>, <c>version</c>) STRG-069 pins. The emitted property set
/// is <c>displayname</c>, <c>resourcetype</c>, <c>getcontentlength</c>, <c>getcontenttype</c>,
/// <c>getetag</c> (quoted), <c>getlastmodified</c> (RFC 1123), <c>creationdate</c> (ISO 8601).
/// Lock / supported-lock / quota-* properties are STRG-070+ territory.
///
/// <para><b>Why our own writer instead of NWebDav's PropFindHandler.</b> NWebDav 0.1.x's
/// dispatcher takes its own <c>IHttpContext</c> and <c>IPropertyManager</c> wiring and its
/// ASP.NET Core adapter package ships a transitively vulnerable
/// <c>Microsoft.AspNetCore.Http 2.0</c> (GHSA-hxrm-9w7p-39cc). A hand-rolled XML writer gives us
/// all the TC-001..TC-006 pins without dragging either abandoned abstraction into the hot path.
/// </para>
///
/// <para><b>XXE posture.</b> This writer emits XML; it never parses the request body. PROPFIND
/// clients may send a <c>&lt;D:propfind&gt;&lt;D:prop&gt;...&lt;/D:prop&gt;&lt;/D:propfind&gt;</c>
/// body to subset the properties they want, but v0.1 ignores that selector and always returns
/// the full property set — clients receive a superset of what they asked for, which is still
/// spec-valid per RFC 4918 §9.1. Because no request XML is parsed, there is no XXE attack surface
/// on this side of the middleware. If a future ticket starts honouring the <c>prop</c> selector,
/// the reader MUST set <c>XmlReaderSettings.DtdProcessing = Prohibit</c> and
/// <c>XmlResolver = null</c>.</para>
/// </summary>
internal static class WebDavResponseWriter
{
    private const string DavNamespace = "DAV:";
    private const string StrgNamespace = "urn:strg:webdav";
    private const string StrgPrefix = "s";

    /// <summary>
    /// Emits the 207 Multi-Status response for a PROPFIND request. The <c>Depth</c> header drives
    /// which items appear:
    /// <list type="bullet">
    ///   <item><description><c>0</c> — target item only.</description></item>
    ///   <item><description><c>1</c> — target + immediate children (for collections).</description></item>
    ///   <item><description><c>infinity</c> — target + all descendants, capped at
    ///     <paramref name="infinityCap"/>. Over the cap returns
    ///     <c>507 Insufficient Storage</c> <i>before</i> any XML is written.</description></item>
    /// </list>
    /// RFC 4918 §9.1 makes <c>infinity</c> the default; we pin <c>1</c> as the default instead so
    /// a Depth-less request from a buggy client can't trigger the heaviest path. Real WebDAV
    /// clients always send an explicit <c>Depth</c> header.
    /// </summary>
    public static async Task WritePropFindAsync(
        HttpContext context,
        IStrgWebDavStoreItem item,
        int infinityCap,
        CancellationToken cancellationToken)
    {
        var depth = context.Request.Headers.TryGetValue("Depth", out var depthHeader)
            ? depthHeader.ToString()
            : "1";

        var isInfinity = string.Equals(depth, "infinity", StringComparison.OrdinalIgnoreCase);

        // For Depth: infinity we short-circuit with 507 BEFORE sending any response bytes. Running
        // the cap check after starting the XML stream would leak partial responses and still hand
        // the client a response shape that looked like a success. The cap check happens against
        // Take(cap + 1).CountAsync(), which stops scanning immediately past the ceiling.
        if (isInfinity && item is IStrgWebDavStoreCollection capCollection)
        {
            var bounded = await capCollection.CountDescendantsBoundedAsync(infinityCap, cancellationToken);
            // + 1 for the collection itself — the cap budget is total items emitted, not just
            // descendants. A 10 000-item cap on a drive with exactly 10 000 descendants would
            // otherwise emit 10 001 <response> elements (root + descendants).
            if (bounded + 1 > infinityCap)
            {
                context.Response.StatusCode = StatusCodes.Status507InsufficientStorage;
                return;
            }
        }

        context.Response.StatusCode = StatusCodes.Status207MultiStatus;
        context.Response.ContentType = "application/xml; charset=utf-8";

        var settings = new XmlWriterSettings
        {
            Async = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            OmitXmlDeclaration = false,
            Indent = false,
        };

        await using var writer = XmlWriter.Create(context.Response.Body, settings);
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(prefix: "D", localName: "multistatus", ns: DavNamespace);
        // Declare the strg: namespace on the root element so every <s:contenthash> / <s:version>
        // descendant resolves without redeclaring it per-response — matters for response size on
        // Depth: infinity, and keeps the XML human-readable.
        await writer.WriteAttributeStringAsync(prefix: "xmlns", localName: StrgPrefix, ns: null, value: StrgNamespace);

        await WriteResponseAsync(writer, context, item);

        if (item is IStrgWebDavStoreCollection collection)
        {
            if (isInfinity)
            {
                await foreach (var descendant in collection.GetDescendantsAsync(cancellationToken))
                {
                    await WriteResponseAsync(writer, context, descendant);
                }
            }
            else if (!string.Equals(depth, "0", StringComparison.Ordinal))
            {
                await foreach (var child in collection.GetChildrenAsync(cancellationToken))
                {
                    await WriteResponseAsync(writer, context, child);
                }
            }
        }

        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
    }

    /// <summary>
    /// Streams the GET/HEAD body for a document. HEAD requests skip the body but still set the
    /// response headers per RFC 7231 §4.3.2 so clients can probe <c>Content-Length</c>,
    /// <c>Content-Type</c>, and <c>ETag</c> without transferring the blob.
    ///
    /// <para><b>Range requests (RFC 7233).</b> A single-range <c>Range: bytes=N-M</c> header
    /// produces a <c>206 Partial Content</c> response with <c>Content-Range: bytes N-M/total</c>
    /// and <c>Content-Length</c> = M-N+1. Multi-range (<c>bytes=0-99,200-299</c>) is not supported
    /// — RFC 7233 §4 lets servers treat such requests as a plain 200 full-body response, which is
    /// what we do. An unsatisfiable range (start past end of file) returns
    /// <c>416 Range Not Satisfiable</c> with an empty body.</para>
    ///
    /// <para><b>Seek via provider.</b> <see cref="Core.Storage.IStorageProvider.ReadAsync"/>
    /// accepts a byte offset so seeking is a provider-level concern — the local FS provider
    /// wraps <c>FileStream.Seek</c>, and S3 future providers will emit a ranged GET. The length
    /// is enforced here by wrapping the source in a byte counter so we stop copying when the
    /// requested window is satisfied even if the provider stream yields more bytes.</para>
    /// </summary>
    public static async Task WriteGetAsync(
        HttpContext context,
        IStrgWebDavStoreDocument document,
        bool includeBody,
        CancellationToken cancellationToken)
    {
        context.Response.ContentType = document.ContentType;
        // Defense-in-depth against stored-XSS via client-uploaded Content-Type: PUT echoes the
        // client's Content-Type straight through to FileItem.MimeType, and without this header a
        // same-tenant attacker who PUTs <script>…</script> with `Content-Type: text/html` would
        // get it rendered inline for any victim navigating /dav/... in an authenticated browser,
        // with access to /graphql on the same origin. `attachment` disposition forces download,
        // not inline render, regardless of Content-Type. Phase-10 global CSP/nosniff middleware
        // will close this at the pipeline root; this is the v0.1 backstop on the WebDAV surface.
        context.Response.Headers[HeaderNames.ContentDisposition] =
            BuildAttachmentDisposition(document.Name);
        context.Response.Headers[HeaderNames.LastModified] =
            document.UpdatedAt.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
        context.Response.Headers[HeaderNames.AcceptRanges] = "bytes";
        if (!string.IsNullOrEmpty(document.ContentHash))
        {
            context.Response.Headers[HeaderNames.ETag] = $"\"{document.ContentHash}\"";
        }

        var total = document.ContentLength;
        var (rangeStart, rangeLength, isPartial, isUnsatisfiable) = TryParseSingleRange(
            context.Request.Headers[HeaderNames.Range].ToString(), total);

        if (isUnsatisfiable)
        {
            // RFC 7233 §4.4 — 416 carries Content-Range with the complete length so the client
            // can recompute an acceptable window.
            context.Response.Headers[HeaderNames.ContentRange] =
                string.Create(CultureInfo.InvariantCulture, $"bytes */{total}");
            context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            return;
        }

        if (isPartial)
        {
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.ContentLength = rangeLength;
            context.Response.Headers[HeaderNames.ContentRange] = string.Create(
                CultureInfo.InvariantCulture,
                $"bytes {rangeStart}-{rangeStart + rangeLength - 1}/{total}");
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentLength = total;
        }

        if (!includeBody)
        {
            return;
        }

        await using var source = await document.OpenReadStreamAsync(cancellationToken);
        if (isPartial && rangeStart > 0 && source.CanSeek)
        {
            source.Seek(rangeStart, SeekOrigin.Begin);
        }

        if (isPartial)
        {
            await CopyBoundedAsync(source, context.Response.Body, rangeLength, cancellationToken);
        }
        else
        {
            await source.CopyToAsync(context.Response.Body, cancellationToken);
        }
    }

    /// <summary>
    /// STRG-070 MED-1 — builds an RFC 6266 <c>Content-Disposition: attachment</c> header that
    /// forces browsers to download the response rather than render it inline. See the call site in
    /// <see cref="WriteGetAsync"/> for the XSS rationale.
    ///
    /// <para><b>Why the dual form.</b> RFC 6266 §4.3 recommends emitting both <c>filename="..."</c>
    /// (ASCII-only quoted-string, understood by every HTTP client including legacy ones) and
    /// <c>filename*=UTF-8''...</c> (RFC 5987 ext-value, preferred by modern browsers). Emitting
    /// only ASCII loses non-Latin filenames on save; emitting only the ext-value form breaks
    /// ancient clients. Both together round-trip correctly everywhere. The <c>filename*</c>
    /// parameter is only appended when the original name actually contains non-ASCII — appending
    /// it unconditionally costs bytes without adding information.</para>
    ///
    /// <para><b>Sanitization.</b> The ASCII fallback replaces control chars, <c>"</c>, and
    /// <c>\</c> with <c>_</c>; those bytes would either break the quoted-string grammar or open
    /// a header-injection avenue if a filename ever slipped past upload-time validation.
    /// <see cref="Uri.EscapeDataString"/> on the ext-value side already percent-escapes everything
    /// outside RFC 3986 unreserved, which is a strict subset of RFC 5987's attr-char — so the raw
    /// UTF-8 name can be fed in without pre-sanitization.</para>
    /// </summary>
    private static string BuildAttachmentDisposition(string filename)
    {
        var asciiSafe = new StringBuilder(filename.Length);
        var hasNonAscii = false;
        foreach (var c in filename)
        {
            if (c > 127)
            {
                hasNonAscii = true;
                asciiSafe.Append('_');
            }
            else if (c < 32 || c == 127 || c == '"' || c == '\\')
            {
                asciiSafe.Append('_');
            }
            else
            {
                asciiSafe.Append(c);
            }
        }

        var fallback = asciiSafe.Length == 0 ? "download" : asciiSafe.ToString();
        if (hasNonAscii)
        {
            var encoded = Uri.EscapeDataString(filename);
            return $"attachment; filename=\"{fallback}\"; filename*=UTF-8''{encoded}";
        }
        return $"attachment; filename=\"{fallback}\"";
    }

    private static (long Start, long Length, bool IsPartial, bool IsUnsatisfiable) TryParseSingleRange(
        string rawRange, long total)
    {
        if (string.IsNullOrWhiteSpace(rawRange))
        {
            return (0, total, false, false);
        }

        const string prefix = "bytes=";
        if (!rawRange.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            // Unknown range unit (RFC 7233 §2.1 only defines "bytes"). Fall back to full-body
            // delivery rather than surfacing a 416 — the client asked for something exotic;
            // giving them the whole resource is spec-permitted and strictly more useful.
            return (0, total, false, false);
        }

        var spec = rawRange[prefix.Length..];
        // Multi-range is spec-legal but complex; we treat it as "full body" per the class doc.
        if (spec.Contains(',', StringComparison.Ordinal))
        {
            return (0, total, false, false);
        }

        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            return (0, total, false, true);
        }

        var startStr = spec[..dash].Trim();
        var endStr = spec[(dash + 1)..].Trim();

        long start;
        long endInclusive;

        if (startStr.Length == 0)
        {
            // Suffix byte range: `bytes=-500` → last 500 bytes.
            if (!long.TryParse(endStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var suffix)
                || suffix <= 0)
            {
                return (0, total, false, true);
            }
            if (total == 0)
            {
                return (0, 0, false, true);
            }
            start = Math.Max(0, total - suffix);
            endInclusive = total - 1;
        }
        else
        {
            if (!long.TryParse(startStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out start)
                || start < 0 || start >= total)
            {
                return (0, total, false, true);
            }

            if (endStr.Length == 0)
            {
                endInclusive = total - 1;
            }
            else
            {
                if (!long.TryParse(endStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out endInclusive)
                    || endInclusive < start)
                {
                    return (0, total, false, true);
                }
                if (endInclusive >= total)
                {
                    endInclusive = total - 1;
                }
            }
        }

        var length = endInclusive - start + 1;
        return (start, length, true, false);
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var remaining = maxBytes;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static async Task WriteResponseAsync(
        XmlWriter writer,
        HttpContext context,
        IStrgWebDavStoreItem item)
    {
        await writer.WriteStartElementAsync("D", "response", DavNamespace);

        await writer.WriteStartElementAsync("D", "href", DavNamespace);
        await writer.WriteStringAsync(BuildHref(context, item));
        await writer.WriteEndElementAsync();

        await writer.WriteStartElementAsync("D", "propstat", DavNamespace);
        await writer.WriteStartElementAsync("D", "prop", DavNamespace);

        await writer.WriteElementStringAsync("D", "displayname", DavNamespace, item.Name);

        await writer.WriteStartElementAsync("D", "resourcetype", DavNamespace);
        if (item.IsCollection)
        {
            // <D:resourcetype><D:collection/></D:resourcetype> — the RFC 4918 signal that tells
            // clients to render this as a folder icon instead of attempting to GET it.
            await writer.WriteStartElementAsync("D", "collection", DavNamespace);
            await writer.WriteEndElementAsync();
        }
        await writer.WriteEndElementAsync();

        if (item is IStrgWebDavStoreDocument document)
        {
            await writer.WriteElementStringAsync(
                "D", "getcontentlength", DavNamespace,
                document.ContentLength.ToString(CultureInfo.InvariantCulture));
            await writer.WriteElementStringAsync(
                "D", "getcontenttype", DavNamespace, document.ContentType);

            // Spec table: getetag is ContentHash WRAPPED IN DOUBLE QUOTES per RFC 7232 §2.3.
            // Clients that compare ETags literally (syntactic match) would treat an unquoted hash
            // as a syntactically-invalid ETag and skip cache revalidation — the quotes are not
            // cosmetic. Files that never completed their hash pass omit the property rather than
            // emit an empty string so clients don't cache on a placeholder.
            if (!string.IsNullOrEmpty(document.ContentHash))
            {
                await writer.WriteElementStringAsync(
                    "D", "getetag", DavNamespace,
                    $"\"{document.ContentHash}\"");
            }
        }

        await writer.WriteElementStringAsync(
            "D", "getlastmodified", DavNamespace,
            item.UpdatedAt.UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
        await writer.WriteElementStringAsync(
            "D", "creationdate", DavNamespace,
            item.CreatedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));

        // Custom strg: dead properties. Only emitted for documents — folders have no meaningful
        // hash and their "version" is just the subtree's own history count, which doesn't belong
        // in a per-item PROPFIND property. STRG-070+ can revisit if real clients need folder
        // versioning signalled here.
        if (item is IStrgWebDavStoreDocument doc)
        {
            if (!string.IsNullOrEmpty(doc.ContentHash))
            {
                await writer.WriteElementStringAsync(
                    StrgPrefix, "contenthash", StrgNamespace, doc.ContentHash);
            }
            await writer.WriteElementStringAsync(
                StrgPrefix, "version", StrgNamespace,
                doc.Version.ToString(CultureInfo.InvariantCulture));
        }

        await writer.WriteEndElementAsync(); // prop
        await writer.WriteElementStringAsync("D", "status", DavNamespace, "HTTP/1.1 200 OK");
        await writer.WriteEndElementAsync(); // propstat

        await writer.WriteEndElementAsync(); // response
    }

    /// <summary>
    /// Reconstructs the full HTTP-exposed href (<c>/dav/{driveName}/{path}</c>) for the
    /// <c>href</c> element. <see cref="HttpContext.Request.PathBase"/> contains the
    /// <c>/dav</c> prefix that <c>app.Map</c> stripped, so concatenating PathBase with the
    /// current request path — or rebuilding from (driveName, itemPath) — gives clients a URL
    /// they can round-trip.
    /// </summary>
    private static string BuildHref(HttpContext context, IStrgWebDavStoreItem item)
    {
        var basePath = context.Request.PathBase.Value ?? "/dav";
        var currentPath = context.Request.Path.Value ?? "/";

        // For the item at the request path itself, echo back what the client sent (normalized
        // to a trailing slash for collections). For children, we compose basePath + driveSegment
        // + item path.
        var driveSegment = ExtractDriveSegment(currentPath);
        var href = string.IsNullOrEmpty(item.Path)
            ? $"{basePath}/{driveSegment}/"
            : $"{basePath}/{driveSegment}/{item.Path}";

        return item.IsCollection && !href.EndsWith('/') ? href + "/" : href;
    }

    private static string ExtractDriveSegment(string requestPath)
    {
        // requestPath is "/{driveName}[/remainder]" because /dav was stripped by app.Map.
        var span = requestPath.AsSpan().TrimStart('/');
        var slashIndex = span.IndexOf('/');
        return slashIndex < 0 ? span.ToString() : span[..slashIndex].ToString();
    }

    /// <summary>
    /// STRG-072 — emits the RFC 4918 §9.10.1 <c>prop</c>/<c>lockdiscovery</c>/<c>activelock</c>
    /// XML body clients expect after a successful LOCK. Also sets the <c>Lock-Token</c> header
    /// (RFC 4918 §10.5). We don't call this for refresh — refresh returns the same shape but
    /// doesn't issue a new token, and the status code is 200 not 201.
    /// </summary>
    public static async Task WriteLockAsync(
        HttpContext context,
        Core.Domain.FileLock fileLock,
        int statusCode,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/xml; charset=utf-8";
        // Lock-Token header only on new LOCK (201); refresh intentionally omits per RFC 4918 §8.10.2.
        if (statusCode == StatusCodes.Status201Created)
        {
            context.Response.Headers["Lock-Token"] = $"<{fileLock.Token}>";
        }

        var buffer = new StringBuilder(512);
        var settings = new XmlWriterSettings
        {
            Async = true,
            OmitXmlDeclaration = false,
            Encoding = Encoding.UTF8,
        };
        await using var writer = XmlWriter.Create(buffer, settings);
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync("D", "prop", DavNamespace);

        WriteActiveLockElement(writer, fileLock);

        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();

        var bytes = Encoding.UTF8.GetBytes(buffer.ToString());
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, cancellationToken);
    }

    private static void WriteActiveLockElement(XmlWriter writer, Core.Domain.FileLock fileLock)
    {
        writer.WriteStartElement("D", "lockdiscovery", DavNamespace);
        writer.WriteStartElement("D", "activelock", DavNamespace);

        // RFC 4918 §14.7 — <locktype><write/></locktype>. v0.1 only supports write locks; shared
        // locks are RFC 4918 §6.2 optional territory and clients that need them will fall back to
        // exclusive.
        writer.WriteStartElement("D", "locktype", DavNamespace);
        writer.WriteElementString("D", "write", DavNamespace, string.Empty);
        writer.WriteEndElement();

        writer.WriteStartElement("D", "lockscope", DavNamespace);
        writer.WriteElementString("D", "exclusive", DavNamespace, string.Empty);
        writer.WriteEndElement();

        // Depth: 0 — we only lock the resource itself, not any descendants. Depth-infinity locks
        // on collections are allowed by RFC 4918 §9.10.4 but rarely used by modern clients and
        // complicate conflict detection; STRG-072 defers them to a v0.2 follow-up.
        writer.WriteElementString("D", "depth", DavNamespace, "0");

        if (!string.IsNullOrEmpty(fileLock.OwnerInfo))
        {
            // Echo raw owner element. WriteString XML-escapes the content so any <, &, > from the
            // client stays inert — the owner body is effectively a black-box string in RFC 4918.
            writer.WriteStartElement("D", "owner", DavNamespace);
            writer.WriteString(fileLock.OwnerInfo);
            writer.WriteEndElement();
        }

        var secondsLeft = Math.Max(1L, (long)(fileLock.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
        writer.WriteElementString(
            "D", "timeout", DavNamespace,
            string.Create(CultureInfo.InvariantCulture, $"Second-{secondsLeft}"));

        writer.WriteStartElement("D", "locktoken", DavNamespace);
        writer.WriteElementString("D", "href", DavNamespace, fileLock.Token);
        writer.WriteEndElement();

        writer.WriteEndElement(); // activelock
        writer.WriteEndElement(); // lockdiscovery
    }

    /// <summary>
    /// STRG-072 — extracts the <c>&lt;D:owner&gt;</c> inner text from a LOCK request body. Returns
    /// <c>null</c> if no owner element is present or the body is empty. The XML reader is hardened
    /// against XXE (DTD processing disabled, external resolver null) because LOCK is the first
    /// WebDAV verb that parses client-supplied XML in this codebase — a lax reader here would be
    /// an SSRF vector.
    /// </summary>
    public static async Task<string?> ReadLockOwnerAsync(Stream body, CancellationToken cancellationToken)
    {
        if (body is null || body == Stream.Null)
        {
            return null;
        }

        var settings = new XmlReaderSettings
        {
            Async = true,
            // XXE defence — RFC 4918 clients never legitimately send DTDs, and honoring them would
            // let an attacker make outbound HTTP requests via external entity references.
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = true,
            IgnoreComments = true,
        };

        try
        {
            using var reader = XmlReader.Create(body, settings);
            while (await reader.ReadAsync())
            {
                if (reader.NodeType == XmlNodeType.Element
                    && string.Equals(reader.LocalName, "owner", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(reader.NamespaceURI, DavNamespace, StringComparison.Ordinal))
                {
                    // Inner XML preserves any structured content (e.g. <href>mailto:...</href>)
                    // which some clients include. We store it verbatim; escaping happens on write
                    // via WriteString.
                    return await reader.ReadInnerXmlAsync();
                }
            }
        }
        catch (XmlException)
        {
            // Malformed body → treat as "no owner element". Per RFC 4918 §9.10 the element is
            // optional anyway, and returning null lets the LOCK succeed with an anonymous owner
            // rather than falsely failing the whole request.
            return null;
        }

        return null;
    }
}
