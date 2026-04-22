using System.Globalization;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Strg.WebDav;

/// <summary>
/// STRG-068 v0.1 PROPFIND / GET responder. Writes the minimum RFC 4918 §14.16 multistatus XML
/// the spec's TC-001 pins on, plus the streaming GET body TC-003 pins on. The property set is
/// deliberately minimal — <c>displayname</c>, <c>resourcetype</c>, <c>getcontentlength</c>,
/// <c>getcontenttype</c>, <c>getlastmodified</c>, <c>creationdate</c>. That's exactly what
/// macOS Finder and Windows Explorer ask for on the initial folder open; broader property
/// support (locktoken, supportedlock, quota, win32-*) is STRG-069's territory.
///
/// <para><b>Why our own writer instead of NWebDav's PropFindHandler.</b> NWebDav 0.1.x's
/// dispatcher takes its own <c>IHttpContext</c> and <c>IPropertyManager</c> wiring and its
/// ASP.NET Core adapter package ships a transitively vulnerable
/// <c>Microsoft.AspNetCore.Http 2.0</c> (GHSA-hxrm-9w7p-39cc). A ~100-line XML writer gives us
/// TC-001 + TC-003 without dragging either abandoned abstraction into the hot path. STRG-070+
/// can revisit if real-world clients need properties this doesn't emit.</para>
/// </summary>
internal static class WebDavResponseWriter
{
    private const string DavNamespace = "DAV:";

    /// <summary>
    /// Emits the 207 Multi-Status response for a PROPFIND request. Depth is inferred from the
    /// <c>Depth</c> header — RFC 4918 §9.1 defaults to <c>infinity</c>, but we clamp at 1 for
    /// this v0.1 slice (Phase-9 memory notes infinity support with a cap is a STRG-070 feature).
    /// </summary>
    public static async Task WritePropFindAsync(
        HttpContext context,
        IStrgWebDavStoreItem item,
        CancellationToken cancellationToken)
    {
        var depth = context.Request.Headers.TryGetValue("Depth", out var depthHeader)
            ? depthHeader.ToString()
            : "1";

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

        await WriteResponseAsync(writer, context, item);

        // Depth 0 stops at the target; Depth 1 (or infinity, clamped) enumerates children of
        // collections. We deliberately DO NOT recurse past one level — infinity expansion on a
        // tenanted drive with millions of files would saturate the request pipeline.
        if (!string.Equals(depth, "0", StringComparison.Ordinal)
            && item is IStrgWebDavStoreCollection collection)
        {
            await foreach (var child in collection.GetChildrenAsync(cancellationToken))
            {
                await WriteResponseAsync(writer, context, child);
            }
        }

        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
    }

    /// <summary>
    /// Streams the GET/HEAD body for a document. <see cref="HttpMethods.IsHead"/> requests skip
    /// the body but still set <c>Content-Length</c> / <c>Content-Type</c> per RFC 7231 §4.3.2.
    /// </summary>
    public static async Task WriteGetAsync(
        HttpContext context,
        IStrgWebDavStoreDocument document,
        bool includeBody,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = document.ContentType;
        context.Response.ContentLength = document.ContentLength;
        context.Response.Headers[HeaderNames.LastModified] =
            document.UpdatedAt.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);

        if (!includeBody)
        {
            return;
        }

        await using var source = await document.OpenReadStreamAsync(cancellationToken);
        await source.CopyToAsync(context.Response.Body, cancellationToken);
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
        }

        await writer.WriteElementStringAsync(
            "D", "getlastmodified", DavNamespace,
            item.UpdatedAt.UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
        await writer.WriteElementStringAsync(
            "D", "creationdate", DavNamespace,
            item.CreatedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));

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
}
