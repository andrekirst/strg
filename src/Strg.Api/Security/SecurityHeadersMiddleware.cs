using Microsoft.Net.Http.Headers;
using Strg.Core.Constants;

namespace Strg.Api.Security;

/// <summary>
/// Applies the strg security response-header set to every HTTP response (STRG-010). Registered
/// via <see cref="UseStrgSecurityHeaders(IApplicationBuilder)"/> and wired in <c>Program.cs</c>
/// BEFORE the <c>/dav</c> map and <c>UseStrgOpenApi</c> so short-circuiting middleware further
/// down the pipeline (Swashbuckle's spec endpoints, WebDAV verb handlers) still emit the headers.
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
/// </summary>
internal static class SecurityHeadersMiddleware
{
    private const string XContentTypeOptionsValue = "nosniff";
    private const string XFrameOptionsValue = "DENY";
    private const string ReferrerPolicyValue = "strict-origin-when-cross-origin";
    private const string PermissionsPolicyValue = "geolocation=(), microphone=(), camera=()";

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
