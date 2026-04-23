using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Strg.Infrastructure.Identity;

/// <summary>
/// Shared normalizer for the address strings Kestrel publishes via <see cref="IServerAddressesFeature"/>.
/// Extracted so the WebDAV Basic-Auth bridge (STRG-073 #141 — same-process /connect/token HttpClient
/// BaseAddress) and the OpenIddict Issuer self-detect (STRG-074 #152) share one wildcard-handling
/// surface rather than two near-identical inline <c>.Replace</c> chains.
///
/// <para><b>Why centralize.</b> Kestrel can bind to wildcard-equivalent addresses that are valid for
/// LISTENING but invalid for emitting / targeting: <c>http://*:5000</c>, <c>http://+:5000</c>,
/// <c>http://[::]:5000</c>, and <c>http://0.0.0.0:5000</c> all mean "accept on every interface."
/// A token carrying <c>iss=http://*:5000</c> is malformed per RFC 3986 URI syntax. A bridge HttpClient
/// with <c>BaseAddress=http://0.0.0.0:5000</c> would route via external DNS rather than loopback.
/// Both consumers need the same four substitutions; keeping the table here prevents the WebDAV side
/// from fixing a case (e.g., <c>0.0.0.0</c> per STRG-073 LOW #143) that the OpenIddict side still
/// leaks — or vice versa.</para>
/// </summary>
public static class ServerAddressNormalizer
{
    /// <summary>
    /// Returns the first Kestrel binding, with wildcards substituted for loopback. Prefers
    /// <c>http://</c> over <c>https://</c> when both are present — same-process callers (the
    /// WebDAV bridge) avoid a TLS handshake + self-signed-cert validation dance, and for the
    /// OpenIddict Issuer the scheme choice is moot because the Issuer must match what tokens are
    /// ISSUED with, which in a single-scheme dev/test deployment is whichever Kestrel happens to
    /// bind. Operators who run multi-scheme production should pin <c>OpenIddict:Issuer</c>
    /// explicitly rather than rely on first-binding self-detect.
    /// </summary>
    /// <returns>Normalized address URI, or <c>null</c> if <see cref="IServerAddressesFeature"/> is
    /// not yet populated (e.g., options materialized before Kestrel finished binding — callers
    /// decide whether that's fatal or whether the OpenIddict-style request-BaseUri fallback
    /// applies).</returns>
    public static string? TryResolve(IServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null || addresses.Count == 0)
        {
            return null;
        }

        var raw = addresses.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? addresses.First();
        return NormalizeWildcards(raw);
    }

    /// <summary>
    /// Substitutes the four wildcard host forms Kestrel emits for LISTENING with the loopback
    /// addresses they must become for EMITTING/TARGETING. <c>://*</c> and <c>://+</c> are Kestrel's
    /// any-interface shorthand; <c>://[::]</c> is the RFC 3986 bracketed form of the IPv6
    /// unspecified address; <c>://0.0.0.0</c> is the IPv4 unspecified address (STRG-073 LOW #143 —
    /// security-reviewer flagged that operators commonly configure <c>ASPNETCORE_URLS=http://0.0.0.0:5000</c>
    /// in containers, and the earlier inline normalizer missed that case).
    /// </summary>
    public static string NormalizeWildcards(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        return raw
            .Replace("://*", "://127.0.0.1", StringComparison.Ordinal)
            .Replace("://+", "://127.0.0.1", StringComparison.Ordinal)
            .Replace("://[::]", "://[::1]", StringComparison.Ordinal)
            .Replace("://0.0.0.0", "://127.0.0.1", StringComparison.Ordinal);
    }
}
