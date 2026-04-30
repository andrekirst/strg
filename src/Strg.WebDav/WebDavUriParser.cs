using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
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

    /// <summary>
    /// STRG-071 — parses a WebDAV <c>Destination</c> header (RFC 4918 §10.3) into an in-drive
    /// path validated by <see cref="StoragePath.Parse"/>. The parser enforces the
    /// "WebDAV does not move/copy across drives or hosts" stance from the STRG-071 spec — a
    /// cross-drive Destination collapses to <see cref="DestinationParseStatus.CrossDrive"/> (502
    /// Bad Gateway), and a cross-host Destination collapses to
    /// <see cref="DestinationParseStatus.CrossHost"/> (also 502, per RFC 4918 §9.8.5/§9.9.4 which
    /// reserves 502 for "destination is on a different server" / cross-realm refusals).
    ///
    /// <para><b>Two accepted shapes (RFC 4918 §10.3).</b>
    /// <list type="bullet">
    ///   <item><description>Absolute URI: <c>http(s)://host[:port]/dav/{drive}/{path}</c>. Host +
    ///     port must match the request's <see cref="HostString"/>. The scheme is NOT compared
    ///     because TLS-termination at a reverse proxy can rewrite <c>https</c> → <c>http</c> on the
    ///     wire to Kestrel; matching only host:port avoids false-positive cross-host rejects in
    ///     that topology.</description></item>
    ///   <item><description>Absolute path: <c>/dav/{drive}/{path}</c>. No host comparison.</description></item>
    /// </list></para>
    ///
    /// <para><b>Drive-name comparison is case-sensitive</b> because drive names are produced by
    /// <see cref="Strg.Core.Domain.Drive"/> seeding/creation and constrained to <c>[a-z0-9-]</c>
    /// at the URL boundary (see <see cref="StrgWebDavMiddleware.ExtractDriveName"/>'s implicit
    /// contract). A case-insensitive compare here would mask a path-traversal-via-case-collision
    /// in a future change to the seed validator.</para>
    /// </summary>
    public static DestinationParseResult ParseDestination(string? destinationHeader, HostString requestHost, string requestDriveName)
    {
        if (string.IsNullOrWhiteSpace(destinationHeader))
        {
            return new DestinationParseResult(DestinationParseStatus.Missing, Path: null);
        }

        var trimmed = destinationHeader.Trim();
        string absolutePath;

        // Check the absolute-path case FIRST. Some Uri.TryCreate implementations accept "/dav/..."
        // as an absolute URI with an empty Host, which would then fail the host comparison below
        // and produce a spurious CrossHost result for what is actually a same-server reference.
        if (trimmed.StartsWith('/'))
        {
            absolutePath = trimmed;
        }
        else if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri) && absoluteUri.IsAbsoluteUri && !string.IsNullOrEmpty(absoluteUri.Host))
        {
            // Match host:port against the request's Host header. requestHost.ToString() includes
            // the port only when non-default; uri.Authority always includes the explicit port —
            // normalise both sides through HostString construction so 80/443 comparisons work.
            var destHost = absoluteUri.IsDefaultPort
                ? new HostString(absoluteUri.Host)
                : new HostString(absoluteUri.Host, absoluteUri.Port);
            if (!string.Equals(destHost.ToString(), requestHost.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return new DestinationParseResult(DestinationParseStatus.CrossHost, Path: null);
            }
            absolutePath = absoluteUri.AbsolutePath;
        }
        else
        {
            return new DestinationParseResult(DestinationParseStatus.Malformed, Path: null);
        }

        // RFC 4918 §8.3: Destination header path segments are percent-encoded.
        var decoded = Uri.UnescapeDataString(absolutePath);

        // Strip the /dav prefix. Anything that doesn't lead with "/dav/" is malformed — no
        // implicit fallback to "treat as in-drive path".
        const string DavPrefix = "/dav/";
        if (!decoded.StartsWith(DavPrefix, StringComparison.Ordinal))
        {
            return new DestinationParseResult(DestinationParseStatus.Malformed, Path: null);
        }

        var afterPrefix = decoded.AsSpan(DavPrefix.Length);
        if (afterPrefix.IsEmpty)
        {
            return new DestinationParseResult(DestinationParseStatus.Malformed, Path: null);
        }

        var slashIndex = afterPrefix.IndexOf('/');
        ReadOnlySpan<char> destDriveSpan;
        ReadOnlySpan<char> remainder;
        if (slashIndex < 0)
        {
            destDriveSpan = afterPrefix;
            remainder = ReadOnlySpan<char>.Empty;
        }
        else
        {
            destDriveSpan = afterPrefix[..slashIndex];
            remainder = afterPrefix[(slashIndex + 1)..];
        }

        if (destDriveSpan.IsEmpty)
        {
            return new DestinationParseResult(DestinationParseStatus.Malformed, Path: null);
        }

        if (!destDriveSpan.SequenceEqual(requestDriveName.AsSpan()))
        {
            return new DestinationParseResult(DestinationParseStatus.CrossDrive, Path: null);
        }

        // Trailing slash on the destination collapses to root: "/dav/{drive}/" is the drive root,
        // which is not a legal MOVE/COPY target. The middleware turns Path=="" into 403 — keeping
        // that decision out of this parser so the parser stays purely about URL shape.
        if (remainder.IsEmpty)
        {
            return new DestinationParseResult(DestinationParseStatus.Ok, Path: string.Empty);
        }

        // Drop a trailing slash (RFC 4918 collections use trailing-slash form). StoragePath.Parse
        // rejects both leading and trailing slashes — we strip the trailing one before validating.
        if (remainder[^1] == '/')
        {
            remainder = remainder[..^1];
        }

        try
        {
            var parsed = StoragePath.Parse(remainder.ToString());
            return new DestinationParseResult(DestinationParseStatus.Ok, parsed.Value);
        }
        catch (StoragePathException)
        {
            return new DestinationParseResult(DestinationParseStatus.InvalidPath, Path: null);
        }
    }
}

/// <summary>
/// Outcome of <see cref="WebDavUriParser.ParseDestination"/>. Each status maps to a distinct HTTP
/// response in the middleware (400 / 502 / Ok-then-dispatch); keeping the discriminator explicit
/// rather than collapsing failures into a single "Invalid" bucket avoids the operator-debugging
/// trap where a 400 shape hides a cross-drive intent.
/// </summary>
public enum DestinationParseStatus
{
    Ok,
    Missing,
    Malformed,
    InvalidPath,
    CrossHost,
    CrossDrive,
}

/// <summary>
/// <see cref="Path"/> is non-null exactly when <see cref="Status"/> is
/// <see cref="DestinationParseStatus.Ok"/>. The empty-string case represents the destination
/// drive root, which the middleware refuses with 403 — see <see cref="WebDavUriParser.ParseDestination"/>'s
/// trailing-slash note. <see cref="IsOk"/> is the load-bearing accessor that flow-narrows
/// <see cref="Path"/> to non-null on the happy branch, removing the bang-suppression every
/// call site would otherwise need.
/// </summary>
public sealed record DestinationParseResult(DestinationParseStatus Status, string? Path)
{
    /// <summary>
    /// <c>true</c> when the parse succeeded. The <see cref="MemberNotNullWhenAttribute"/>
    /// teaches the C# flow analyser that <see cref="Path"/> is non-null on this branch — a
    /// caller writing <c>if (result.IsOk) Use(result.Path)</c> compiles without nullability
    /// suppressions.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Path))]
    public bool IsOk => Status == DestinationParseStatus.Ok;
}
