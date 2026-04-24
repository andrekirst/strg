using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Strg.Api.OpenApi;
using Strg.Integration.Tests.Auth;
using Xunit;

namespace Strg.Integration.Tests.OpenApi;

/// <summary>
/// STRG-009 — end-to-end pins on the OpenAPI 3.1 surface exposed by <c>Strg.Api</c>.
///
/// <para>
/// TC-001 / TC-001b / TC-001c / TC-002 / TC-004 drive the real ASP.NET Core pipeline through
/// <see cref="StrgWebApplicationFactory"/> so the assertions cover the production wiring
/// (<c>AddStrgOpenApi()</c>, <c>UseStrgOpenApi(isDevelopment)</c>, <c>IncludeXmlComments</c>,
/// <c>OpenApiVersion = OpenApi3_1</c>) — not a hand-rolled minimal host.
/// </para>
///
/// <para>
/// TC-003 (UI NOT accessible in production) uses a minimal <see cref="WebApplication"/> with
/// <c>EnvironmentName = "Production"</c> instead of reusing
/// <see cref="StrgWebApplicationFactory"/> (which forces <c>Development</c> because production
/// OpenIddict needs an X.509 cert and production GraphQL subscriptions need Redis — neither is
/// available in CI). The minimal host wires ONLY the OpenAPI surface, which directly tests the
/// env gate in isolation without fighting the full app stack.
/// </para>
/// </summary>
public sealed class OpenApiSpecTests(StrgWebApplicationFactory factory) : IClassFixture<StrgWebApplicationFactory>
{
    /// <summary>
    /// TC-001 + AC1 — <c>GET /openapi/v1.json</c> returns 200 with <c>application/json</c> AND the
    /// payload is OpenAPI 3.1. Swashbuckle 10.x still defaults to 3.0, so the 3.1 assertion here
    /// is a regression pin for <c>OpenApiVersion = OpenApi3_1</c> in
    /// <see cref="OpenApiApplicationBuilderExtensions.UseStrgOpenApi"/>; a weaker check that only
    /// parsed "openapi starts with 3" would silently pass against a 3.0 default and the issue's
    /// AC1 ("valid OpenAPI 3.1 JSON") would go untested.
    /// </summary>
    [Fact]
    public async Task Get_openapi_v1_json_returns_200_and_is_openapi_3_1()
    {
        using var client = factory.CreateClient();

        // Anonymous — the Swashbuckle middleware is mounted BEFORE UseAuthentication/Authorization
        // in Program.cs so the global FallbackPolicy = RequireAuthenticatedUser cannot 401 the
        // spec. Pin that contract.
        client.DefaultRequestHeaders.Authorization.Should().BeNull(
            "spec must be anonymously reachable — UI/tooling fetches it without bearer credentials");

        using var response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType.Should().NotBeNull();
        response.Content.Headers.ContentType!.MediaType.Should().StartWith("application/json");

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        root.TryGetProperty("openapi", out var versionElement).Should().BeTrue(
            "every OpenAPI document has a top-level 'openapi' field");
        var version = versionElement.GetString();
        version.Should().NotBeNullOrEmpty();
        version!.Should().StartWith("3.1",
            "OpenApiVersion = OpenApi3_1 must emit a 3.1.* document — not the Swashbuckle 3.0 default");
    }

    /// <summary>
    /// TC-001b (part 1) + AC2 — the spec advertises a Bearer HTTP/JWT security scheme under
    /// <c>components.securitySchemes.Bearer</c>. The HTTP/bearer shape (not OAuth2 flow with
    /// client_secret in URL) is a hard requirement from the STRG-009 security checklist.
    /// </summary>
    [Fact]
    public async Task Get_openapi_v1_json_defines_bearer_jwt_security_scheme_in_components()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // components.securitySchemes.Bearer — shape set by AddSecurityDefinition("Bearer", ...).
        root.TryGetProperty("components", out var components).Should().BeTrue(
            "Swashbuckle always emits components when at least one security scheme is registered");
        components.TryGetProperty("securitySchemes", out var schemes).Should().BeTrue();
        schemes.TryGetProperty("Bearer", out var bearer).Should().BeTrue(
            "the security scheme name is 'Bearer' — matches OpenApiServiceCollectionExtensions.BearerSchemeName");

        bearer.GetProperty("type").GetString().Should().Be("http",
            "HTTP/bearer scheme — NOT an OAuth2 flow (security checklist requirement)");
        bearer.GetProperty("scheme").GetString().Should().Be("bearer");
        bearer.GetProperty("bearerFormat").GetString().Should().Be("JWT");
    }

    /// <summary>
    /// TC-001b (part 2) + AC2 — the global <c>security</c> array references the <c>Bearer</c>
    /// scheme, making Bearer auth the default requirement for every operation.
    ///
    /// <para>
    /// <b>Why this is split from the scheme-definition test:</b> the definition and the global
    /// requirement are distinct wire concerns. An implementation can define the scheme
    /// correctly (part 1 above) while the global requirement serializes as an empty object
    /// <c>[{}]</c> — which in OpenAPI 3.1 semantically means "no auth required globally" and
    /// makes the scheme definition cosmetic. Separating the two makes the failure mode
    /// diagnosable: if this test fails and the other passes, the bug is in
    /// <c>AddSecurityRequirement</c>, not <c>AddSecurityDefinition</c>.
    /// </para>
    ///
    /// <para>
    /// Swashbuckle 10.x + Microsoft.OpenApi 2.x: using <c>new OpenApiSecuritySchemeReference(name)</c>
    /// as a dictionary key in the requirement does NOT round-trip to
    /// <c>{ "Bearer": [] }</c> — it emits an empty object. The expected construction is
    /// <c>new OpenApiSecurityScheme { Reference = new OpenApiReference { Type =
    /// ReferenceType.SecurityScheme, Id = "Bearer" } }</c> as the dictionary key.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Get_openapi_v1_json_top_level_security_array_references_bearer_scheme()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Top-level security — array of requirement objects, each a map { schemeName: [scopes] }.
        // An empty requirement object `{}` in the array is NOT acceptable: in OpenAPI 3.1 that
        // means "no auth required" (the canonical anonymous-override construct), which would
        // defeat AC2's intent.
        root.TryGetProperty("security", out var securityArray).Should().BeTrue(
            "global Bearer requirement must be registered via AddSecurityRequirement");
        securityArray.ValueKind.Should().Be(JsonValueKind.Array);

        var hasBearerRequirement = securityArray.EnumerateArray()
            .Any(requirement => requirement.TryGetProperty("Bearer", out _));
        hasBearerRequirement.Should().BeTrue(
            "at least one top-level security requirement must reference the 'Bearer' scheme — "
            + "an empty requirement `{}` means 'no auth' in OpenAPI 3.1 and would make the scheme "
            + "definition cosmetic");
    }

    /// <summary>
    /// TC-001c — <c>GET /openapi/v1.yaml</c> serves the same spec as YAML. Proves BOTH the
    /// route template's <c>{extension:regex(^(json|ya?ml)$)}</c> constraint AND the 3.1
    /// serialization path work. YAML is a separate Swashbuckle code path from JSON, so a
    /// regression in one does not imply the other.
    /// </summary>
    [Fact]
    public async Task Get_openapi_v1_yaml_returns_200_and_yaml_starts_with_openapi_3_1()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.yaml");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        // YAML syntax: `openapi:` at document start, then a 3.1.x value (possibly YAML-quoted).
        // Microsoft.OpenApi 2.x emits `openapi: '3.1.1'` — the version is a quoted YAML string,
        // which is valid (YAML serializers legitimately quote values that would otherwise parse
        // as non-strings). The regex accepts both `openapi: 3.1*` and `openapi: '3.1*'` so a
        // future writer change that drops the quoting does not flake this test. Trim guards
        // against a leading BOM/whitespace that some YAML serializers emit.
        //
        // The contract under test is "YAML format AND OpenAPI 3.1 version" — not a specific
        // quoting style. Matching the JSON opening `{` shape would also defeat the YAML pin.
        var trimmed = body.Trim();
        Regex.IsMatch(trimmed, @"^openapi:\s*'?3\.1").Should().BeTrue(
            $"/openapi/v1.yaml must serve YAML (not JSON) AND must be OpenAPI 3.1.x — "
            + "both the route-template extension and the OpenApiVersion = OpenApi3_1 flag "
            + $"are under test here. Body starts with: '{trimmed[..Math.Min(40, trimmed.Length)]}'");
        // Negative pin: JSON would start with `{` — reject any document that does.
        trimmed.Should().NotStartWith("{",
            "/openapi/v1.yaml must not serve a JSON payload with a YAML content-type");
    }

    /// <summary>
    /// TC-002 + AC3 — Swagger UI is mounted at <c>/openapi/ui</c> in Development.
    /// <see cref="StrgWebApplicationFactory"/> forces Development, so this is the positive case.
    /// Swashbuckle's UI registration typically redirects <c>/openapi/ui</c> →
    /// <c>/openapi/ui/index.html</c>; the default HttpClient follows redirects, so asserting
    /// 200 + <c>text/html</c> on the final response covers both the prefix and the asset.
    /// </summary>
    [Fact]
    public async Task Get_openapi_ui_in_development_returns_200_html()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/ui");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "Development builds must serve the interactive Swagger UI at /openapi/ui");
        response.Content.Headers.ContentType.Should().NotBeNull();
        response.Content.Headers.ContentType!.MediaType.Should().StartWith("text/html",
            "the UI is an HTML document — pin the content-type so a regression that swaps the "
            + "UI for a JSON redirect page would surface here");
    }

    /// <summary>
    /// TC-003 + AC4 + Security Checklist — Swagger UI must NOT be reachable in Production. We
    /// mount a minimal <see cref="WebApplication"/> with <c>EnvironmentName = "Production"</c>,
    /// call ONLY the OpenAPI wiring (<c>AddStrgOpenApi</c> + <c>UseStrgOpenApi(false)</c>),
    /// and assert 404 on both the prefix and the index asset.
    ///
    /// <para>
    /// Asserting 404 on <c>/openapi/ui/index.html</c> in addition to the bare prefix guards
    /// against a future regression that flips the env gate from "don't register
    /// UseSwaggerUI" to "return 403" — the latter would still 404 the bare prefix but serve
    /// the asset. The production contract is that the middleware is never mounted at all.
    /// </para>
    ///
    /// <para>
    /// We use the minimal-host approach (rather than subclassing
    /// <see cref="StrgWebApplicationFactory"/> with Production env) because
    /// <c>StrgWebApplicationFactory</c> forces Development for a reason: production OpenIddict
    /// requires an X.509 signing cert and production GraphQL subscriptions require Redis —
    /// neither is available in CI. The minimal host tests the env gate in isolation without
    /// fighting the rest of the stack.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Get_openapi_ui_in_production_returns_404()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
        });

        builder.WebHost.UseTestServer();

        // ONLY the OpenAPI surface — no auth, no DB, no controllers. The production gate lives in
        // UseStrgOpenApi(isDevelopment: false), which simply never calls UseSwaggerUI(). Nothing
        // else in the pipeline can mask or amplify that behavior here.
        builder.Services.AddStrgOpenApi();

        await using var app = builder.Build();
        app.UseStrgOpenApi(enableUi: false);

        await app.StartAsync();
        try
        {
            using var client = app.GetTestClient();

            using var uiResponse = await client.GetAsync("/openapi/ui");
            uiResponse.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "Production deployments must not mount the Swagger UI — the middleware is gated "
                + "at registration time (not a runtime 403), so /openapi/ui has no matching route");

            // Second-level pin: the index asset must ALSO 404. A regression that switched the gate
            // from "don't register" to "return 403" on the prefix would still let the index.html
            // asset leak through this path.
            using var indexResponse = await client.GetAsync("/openapi/ui/index.html");
            indexResponse.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "even the static UI asset must be unreachable in production — UseSwaggerUI must "
                + "never be registered, not merely gated behind a 403");

            // Sanity pin: the SPEC endpoints are still available in production (by design — the
            // env gate applies to the UI only, so machine-readable clients keep working).
            using var specResponse = await client.GetAsync("/openapi/v1.json");
            specResponse.StatusCode.Should().Be(HttpStatusCode.OK,
                "spec endpoints are NOT env-gated — only the interactive UI is");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    /// <summary>
    /// Defence-in-depth gate — <c>UseStrgOpenApi(enableUi: true)</c> MUST serve the UI even
    /// when the host environment is <c>Production</c>, so that operators who want the UI in
    /// a non-development environment can opt in via <c>Strg:OpenApi:UiEnabled</c> without
    /// lying about the ASPNETCORE_ENVIRONMENT. This pins the negation of TC-003: the env
    /// itself is NOT the gate — the <c>enableUi</c> flag is, and it is resolved upstream in
    /// <c>Program.cs</c> from the explicit config key. A regression that moved the gate back
    /// to <c>IsDevelopment()</c> inside the extension would fail here because the env is
    /// Production.
    /// </summary>
    [Fact]
    public async Task Get_openapi_ui_is_served_when_enableUi_true_even_in_production_env()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddStrgOpenApi();

        await using var app = builder.Build();
        app.UseStrgOpenApi(enableUi: true);

        await app.StartAsync();
        try
        {
            using var client = app.GetTestClient();

            // Assert on the index asset directly — Swashbuckle's UI middleware redirects
            // /openapi/ui → /openapi/ui/index.html with a 301, and TestServer's HttpClient
            // does not follow redirects to relative URLs. Pinning the asset path matches the
            // negative pin used in TC-003 and removes the redirect-handling flake.
            using var indexResponse = await client.GetAsync("/openapi/ui/index.html");

            indexResponse.StatusCode.Should().Be(HttpStatusCode.OK,
                "enableUi=true MUST mount the UI regardless of host environment — the "
                + "config key is the sole gate, so an operator with Strg:OpenApi:UiEnabled=true "
                + "can surface the UI in any environment (the failure mode we are protecting "
                + "against is the INVERSE: environment flipping to Development without the "
                + "config key should NOT expose the UI, which TC-003 already pins)");
            indexResponse.Content.Headers.ContentType!.MediaType.Should().StartWith("text/html");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    /// <summary>
    /// TC-004 + AC5 — XML /// doc comments surface in the spec. <c>DriveEndpoints</c> was
    /// annotated with both <c>.WithSummary("List drives for the current tenant.")</c> on the
    /// route builder AND <c>/// &lt;summary&gt;</c> on the class/method.
    ///
    /// <para>
    /// <b>What the spec actually contains (empirically verified against the generated payload):</b>
    /// Swashbuckle 10.x + ASP.NET Core's endpoint metadata gives the method's
    /// <c>/// &lt;summary&gt;</c> PRECEDENCE over the route-builder's <c>.WithSummary(...)</c>
    /// when both are present — so the spec emits the /// text ("Returns every non-deleted
    /// drive visible to the current tenant. Storage credentials (ProviderConfig) are stripped
    /// from the response.") as the operation summary, and the .WithSummary("List drives...")
    /// text does NOT appear. The precedence choice is Swashbuckle's, not our code's, and is
    /// out of scope for this test.
    /// </para>
    ///
    /// <para>
    /// The load-bearing assertion for AC5 is "XML doc comments on endpoint methods appear in
    /// spec". We pick "Returns every non-deleted drive" — a 5-word unique phrase that appears
    /// ONLY in the <c>ListDrives</c> method's <c>/// summary</c> in
    /// <see cref="Strg.Api.Endpoints.DriveEndpoints"/> and therefore cannot come from anywhere
    /// else (not from <c>.WithSummary</c>, not from any other endpoint's XML docs, not from
    /// the class-level summary). If this text reaches the spec, <c>IncludeXmlComments</c> is
    /// correctly wired. If a future contributor removes <c>GenerateDocumentationFile=true</c>
    /// or <c>IncludeXmlComments</c>, or deletes the <c>/// summary</c> on ListDrives, this
    /// assertion fails and AC5 surfaces as broken.
    /// </para>
    ///
    /// <para>
    /// Path is <c>/api/v1/drives</c> (no trailing slash): <c>MapGroup("/api/v1/drives").MapGet("/", ...)</c>
    /// renders as <c>/api/v1/drives</c> in the spec — the group + sub-route combination strips
    /// the trailing slash at route-pattern resolution time.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Get_openapi_v1_json_contains_drive_endpoint_summary_from_xml_docs()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        root.TryGetProperty("paths", out var paths).Should().BeTrue();
        paths.TryGetProperty("/api/v1/drives", out var drivesPath)
            .Should().BeTrue("MapGroup(\"/api/v1/drives\").MapGet(\"/\", ListDrives) renders as /api/v1/drives");
        drivesPath.TryGetProperty("get", out var getOp)
            .Should().BeTrue("the GET list-drives operation must appear under /api/v1/drives");

        // Serialize the operation back to string and grep — we care about the XML doc text being
        // present somewhere on the operation (summary, description, or response doc), not about
        // which exact field Swashbuckle lands it in (precedence between .WithSummary and ///
        // summary has shifted across versions).
        var operationText = getOp.GetRawText();
        operationText.Should().Contain("Returns every non-deleted drive",
            "the ListDrives method's /// summary must reach the spec via IncludeXmlComments — "
            + "this phrase appears ONLY in that XML doc comment, so its presence pins "
            + "GenerateDocumentationFile + IncludeXmlComments end-to-end");
    }
}
