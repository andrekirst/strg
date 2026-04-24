using System.Reflection;
using Microsoft.OpenApi;

namespace Strg.Api.OpenApi;

/// <summary>
/// STRG-009 — Swashbuckle registration. Isolated from <c>Program.cs</c> so the wiring can
/// evolve independently (spec-version bumps, additional security schemes, servers block for
/// multi-environment deployments) without churn in the composition root.
/// </summary>
internal static class OpenApiServiceCollectionExtensions
{
    internal const string DocumentName = "v1";
    internal const string BearerSchemeName = "Bearer";

    /// <summary>
    /// Registers <see cref="Swashbuckle.AspNetCore.SwaggerGen.SwaggerGenOptions"/> with the
    /// strg-specific <see cref="OpenApiInfo"/>, a global Bearer JWT security requirement, and
    /// the XML-doc comment file produced by <c>GenerateDocumentationFile=true</c>. The Bearer
    /// scheme deliberately uses HTTP/bearer (not an OAuth2 flow) so the spec does NOT advertise
    /// a client_secret in a URL — STRG-009 security checklist requirement.
    /// </summary>
    public static IServiceCollection AddStrgOpenApi(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc(DocumentName, new OpenApiInfo
            {
                Title = "strg API",
                Version = DocumentName,
                Description = "Self-hosted cloud storage platform",
                License = new OpenApiLicense
                {
                    Name = "Apache 2.0",
                    Identifier = "Apache-2.0"
                }
            });

            options.AddSecurityDefinition(BearerSchemeName, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "JWT issued by the /connect/token endpoint. "
                    + "Example: Authorization: Bearer eyJhbGci..."
            });

            // Microsoft.OpenApi v2's OpenApiSecurityRequirement.SerializeInternal filters entries
            // through CanSerializeSecurityScheme, which DROPS any key whose reference cannot
            // resolve its Target (i.e. HostDocument is null OR the named scheme is not in
            // document.Components.SecuritySchemes). A requirement built without the host
            // document silently serializes as `security: [{}]` — which in OpenAPI 3.1 means
            // "no auth required" and makes the scheme definition cosmetic (the failure mode is
            // pinned by OpenApiSpecTests.Get_openapi_v1_json_top_level_security_array_references_bearer_scheme).
            // Passing `doc` as the hostDocument is what threads the reference back to the
            // Components entry that AddSecurityDefinition registered above.
            options.AddSecurityRequirement(doc => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(BearerSchemeName, doc)] = []
            });

            // Pull /// XML-doc comments into the spec so TC-004 and AC "XML doc comments on
            // endpoint methods appear in spec" hold. Scoped to this assembly — sibling projects
            // deliberately do NOT emit XML docs (see Strg.Api.csproj rationale).
            //
            // RS1035 ("Do not do file IO in analyzers") is suppressed here: the rule is
            // activated repo-wide by EnforceExtendedAnalyzerRules=true, which the Roslyn docs
            // target at analyzer DLLs — Strg.Api is a Web app, not a Roslyn analyzer, so the
            // rationale behind the rule (don't break incremental compilation) does not apply.
            // The File.Exists guard is here because Swashbuckle's IncludeXmlComments throws
            // FileNotFoundException if the path is missing (e.g. during a single-project test
            // host that bypasses the generate-docs MSBuild step).
            var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
#pragma warning disable RS1035
            var xmlExists = File.Exists(xmlPath);
#pragma warning restore RS1035
            if (xmlExists)
            {
                options.IncludeXmlComments(xmlPath);
            }
        });

        return services;
    }
}
