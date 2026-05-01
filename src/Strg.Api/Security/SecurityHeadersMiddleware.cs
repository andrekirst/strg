using Microsoft.Net.Http.Headers;
using Strg.Core.Constants;

namespace Strg.Api.Security;

/// <summary>
/// Applies the strg security response-header set to every HTTP response (STRG-010 + STRG-084).
/// Registered via <see cref="UseStrgSecurityHeaders(IApplicationBuilder)"/> and wired in
/// <c>Program.cs</c> BEFORE the <c>/dav</c> map and <c>UseStrgOpenApi</c> so short-circuiting
/// middleware further down the pipeline (Swashbuckle's spec endpoints, WebDAV verb handlers)
/// still emit the headers.
///
/// <para>
/// Headers are attached via <see cref="HttpResponse.OnStarting(Func{object, Task}, object)"/>
/// rather than direct assignment after <c>await next()</c>. Downstream handlers — Swashbuckle,
/// <see cref="Results.File(System.IO.Stream, string?, string?, System.DateTimeOffset?, EntityTagHeaderValue?, bool)"/>,
/// the OpenIddict token endpoint — frequently flush the response before control returns here,
/// and a direct assignment after <c>next</c> would no-op on those paths. <c>OnStarting</c>
/// fires at the moment headers are about to be written, which is the only reliable moment to
/// stamp them on every response the pipeline can produce.
/// </para>
///
/// <para>
/// STRG-084 adds three headers on top of the STRG-010 baseline: a <c>default-src 'none'</c>
/// Content Security Policy with <c>frame-ancestors 'none'</c>, a legacy
/// <c>X-Permitted-Cross-Domain-Policies: none</c> directive, and the
/// <c>interest-cohort=()</c> token in <c>Permissions-Policy</c> to opt out of the FLoC /
/// Topics API on every response. The CSP is scoped for a pure-API host: every directive
/// inherits <c>'none'</c> via <c>default-src</c>, so any browser that lands on a strg
/// response cannot fetch scripts, stylesheets, images, or fonts. The Swagger UI is the only
/// HTML surface strg serves and it uses inline scripts that this CSP blocks — the
/// development-time UX hit is intentional per the issue ("API-specific CSP: no scripts, no
/// inline, no iframes"); operators who need an interactive UI can override CSP for
/// <c>/openapi/ui</c> in their own deployment.
/// </para>
/// </summary>
internal static class SecurityHeadersMiddleware
{
    private const string XContentTypeOptionsValue = "nosniff";
    private const string XFrameOptionsValue = "DENY";
    private const string ReferrerPolicyValue = "strict-origin-when-cross-origin";

    // STRG-084 — interest-cohort opts out of FLoC / Topics API cohort calculations on this
    // origin. Browsers that ignored Permissions-Policy for cohort assignment have all been
    // retired, but the directive remains the canonical opt-out signal.
    private const string PermissionsPolicyValue =
        "camera=(), microphone=(), geolocation=(), interest-cohort=()";

    // STRG-084 — strict default-src 'none' is the API-host shape. frame-ancestors 'none'
    // duplicates the X-Frame-Options: DENY guarantee for browsers that honour CSP but not
    // the legacy header (Safari < 15.4 in particular). The trailing semicolon mirrors the
    // issue spec verbatim.
    private const string ContentSecurityPolicyValue = "default-src 'none'; frame-ancestors 'none';";

    // STRG-084 — bars Adobe Flash / Acrobat plug-ins from honouring any cross-domain policy
    // file. Defence-in-depth against legacy clients that still consult crossdomain.xml.
    private const string XPermittedCrossDomainPoliciesValue = "none";

    /// <summary>
    /// Registers the strg security-header middleware. See the type-level remarks for the
    /// placement constraint relative to short-circuiting middleware.
    /// </summary>
    public static IApplicationBuilder UseStrgSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(static (context, next) =>
        {
            context.Response.OnStarting(static state =>
            {
                var response = (HttpResponse)state;
                var headers = response.Headers;

                headers[HeaderNames.XContentTypeOptions] = XContentTypeOptionsValue;
                headers[HeaderNames.XFrameOptions] = XFrameOptionsValue;
                headers[StrgHeaderNames.ReferrerPolicy] = ReferrerPolicyValue;
                headers[StrgHeaderNames.PermissionsPolicy] = PermissionsPolicyValue;
                headers[HeaderNames.ContentSecurityPolicy] = ContentSecurityPolicyValue;
                headers[StrgHeaderNames.XPermittedCrossDomainPolicies] = XPermittedCrossDomainPoliciesValue;

                // Defence-in-depth strip. Kestrel's default Server header is suppressed at the
                // host level (ConfigureKestrel(AddServerHeader=false) in Program.cs); removing
                // here catches a reverse-proxy or downstream middleware that re-introduced
                // either header. X-Powered-By is not emitted by Kestrel but IIS and some
                // proxies inject it.
                headers.Remove(HeaderNames.Server);
                headers.Remove(HeaderNames.XPoweredBy);

                return Task.CompletedTask;
            }, context.Response);

            return next(context);
        });
    }
}
