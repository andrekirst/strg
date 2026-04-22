using Strg.Core.Storage;

namespace Strg.WebDav;

/// <summary>
/// Extracts the in-drive resource path from a <c>/dav/{driveName}/...</c> URL segment and
/// validates it through <see cref="StoragePath.Parse"/>. This is the exact same fail-closed
/// gate that GraphQL mutations and REST endpoints go through before reaching
/// <see cref="IStorageProvider"/> — the WebDAV URL is untrusted input identical to a request
/// body, so the discipline has to be identical.
///
/// <para><b>Why the two-layer parse.</b> <see cref="StrgWebDavMiddleware"/> already strips
/// <c>/dav</c> via <c>app.Map</c>, so <see cref="HttpContext.Request.Path"/> arrives as
/// <c>/{driveName}/{...}</c>. The middleware's drive resolver consumes the first segment; this
/// helper consumes the remainder and validates it. If the remainder contains <c>..</c>,
/// <c>%00</c>, or a UNC-style backslash, <see cref="StoragePath.Parse"/> throws
/// <see cref="StoragePathException"/>, which the middleware translates to
/// <see cref="Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest"/> — TC-004's pin.</para>
/// </summary>
public static class WebDavUriParser
{
    /// <summary>
    /// Returns the validated in-drive path (empty string for the drive root), or throws
    /// <see cref="StoragePathException"/> for unsafe inputs. The <paramref name="rawRequestPath"/>
    /// is the value of <c>HttpContext.Request.Path</c> inside the <c>/dav</c>-mapped branch, i.e.
    /// starts with <c>/{driveName}[/remainder]</c>.
    /// </summary>
    public static string ExtractValidatedPath(string rawRequestPath)
    {
        ArgumentNullException.ThrowIfNull(rawRequestPath);

        // rawRequestPath begins with "/{driveName}" — strip that, then anything after is the
        // in-drive path. A trailing slash on the drive root (e.g. "/my-drive/") collapses to "".
        var span = rawRequestPath.AsSpan();
        if (span.Length == 0 || span[0] != '/')
        {
            throw new StoragePathException($"Invalid WebDAV path: {rawRequestPath}");
        }

        span = span[1..];
        var slashIndex = span.IndexOf('/');
        if (slashIndex < 0)
        {
            return string.Empty;
        }

        var remainder = span[(slashIndex + 1)..];
        if (remainder.IsEmpty)
        {
            return string.Empty;
        }

        // Fail-closed: any traversal, null byte, or reserved name trips StoragePath.Parse.
        return StoragePath.Parse(remainder.ToString()).Value;
    }
}
