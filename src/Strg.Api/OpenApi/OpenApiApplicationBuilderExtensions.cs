using Microsoft.OpenApi;

namespace Strg.Api.OpenApi;

/// <summary>
/// STRG-009 — wires the Swashbuckle middleware (spec endpoints + Swagger UI).
///
/// <para>
/// Placement in the pipeline matters: both middlewares are registered BEFORE
/// <c>UseAuthentication()</c>/<c>UseAuthorization()</c> so the terminal <c>FallbackPolicy =
/// RequireAuthenticatedUser</c> in <c>Program.cs</c> cannot 401 anonymous spec requests.
/// Swashbuckle's middlewares match by path, not by endpoint routing, so they short-circuit
/// before the auth stack runs.
/// </para>
///
/// <para>
/// OpenAPI 3.1 output is opt-in in Swashbuckle 10.x (default is still 3.0); the
/// <c>OpenApiVersion</c> set below is what actually makes <c>/openapi/v1.json</c> start with
/// <c>"openapi": "3.1.*"</c> — TC-001 asserts against that shape.
/// </para>
/// </summary>
internal static class OpenApiApplicationBuilderExtensions
{
    // Single source of truth for the JSON spec URL. Derived from DocumentName rather than
    // duplicating the literal — keeps the SwaggerEndpoint reference in sync with the
    // RouteTemplate if DocumentName ever moves off "v1".
    private const string JsonPath = $"/openapi/{OpenApiServiceCollectionExtensions.DocumentName}.json";
    private const string UiRoutePrefix = "openapi/ui";

    /// <summary>
    /// Serves <c>/openapi/v1.json</c> and <c>/openapi/v1.yaml</c> (always) and the interactive
    /// Swagger UI at <c>/openapi/ui</c> when <paramref name="enableUi"/> is <c>true</c>.
    /// Callers MUST default <paramref name="enableUi"/> to <c>false</c> for production
    /// deployments — the security checklist requires the UI to be unreachable outside dev,
    /// and gating at registration time (rather than serving a 403) means the route returns
    /// 404 and static assets are never served.
    /// </summary>
    /// <param name="app">The pipeline builder.</param>
    /// <param name="enableUi">
    /// Whether to mount the interactive Swagger UI. The upstream <c>Program.cs</c> resolves
    /// this from the <c>Strg:OpenApi:UiEnabled</c> config key, falling back to
    /// <c>IWebHostEnvironment.IsDevelopment()</c>. The explicit config key exists so an
    /// operator who accidentally ships a Development-branded container to production cannot
    /// leak the UI — they must also flip a named config value.
    /// </param>
    public static IApplicationBuilder UseStrgOpenApi(this IApplicationBuilder app, bool enableUi)
    {
        app.UseSwagger(options =>
        {
            // Swashbuckle 10's default template "/swagger/{documentName}/swagger.{extension:regex(^(json|ya?ml)$)}"
            // handles JSON and YAML in one registration via the {extension} placeholder. Keeping
            // the same placeholder lets both /openapi/v1.json and /openapi/v1.yaml resolve from
            // a single UseSwagger() call.
            options.RouteTemplate = "openapi/{documentName}.{extension:regex(^(json|ya?ml)$)}";
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
        });

        if (enableUi)
        {
            app.UseSwaggerUI(options =>
            {
                options.SwaggerEndpoint(JsonPath, "strg API v1");
                options.RoutePrefix = UiRoutePrefix;
                options.DocumentTitle = "strg API — OpenAPI";
            });
        }

        return app;
    }
}
