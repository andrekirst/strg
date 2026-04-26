using FluentValidation;
using Strg.Api.HealthChecks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using StackExchange.Redis;
using Strg.Api.Auth;
using Strg.Api.Cors;
using Strg.Api.Endpoints;
using Strg.Api.OpenApi;
using Strg.Api.RateLimiting;
using Strg.Api.Security;
using Strg.Application.Abstractions;
using Strg.Application.DependencyInjection;
using Strg.Core.Auditing;
using Strg.Core.Domain;
using Strg.Core.Identity;
using Strg.Core.Services;
using Strg.Core.Storage;
using Strg.Infrastructure.Auditing;
using Strg.Infrastructure.BackgroundJobs;
using Strg.GraphQl.DataLoaders;
using Strg.GraphQl.Errors;
using Strg.GraphQl.Mutations;
using Strg.GraphQl.Queries;
using Strg.GraphQl.Types;
using GraphQlDriveType = Strg.GraphQl.Types.DriveType;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.HealthChecks;
using Strg.Infrastructure.Identity;
using Strg.Infrastructure.Messaging;
using Strg.Infrastructure.Observability;
using Strg.Infrastructure.Services;
using Strg.Infrastructure.Storage;
using Strg.Infrastructure.Upload;
using Strg.Infrastructure.Versioning;
using Strg.WebDav;

var builder = WebApplication.CreateBuilder(args);

// STRG-010 — suppress Kestrel's default `Server: Kestrel` response header. The security-headers
// middleware cannot do this alone: Kestrel writes the Server header at the connection layer,
// AFTER HttpResponse.OnStarting callbacks fire, so Response.Headers.Remove("Server") from user
// middleware is a no-op against the default. AddServerHeader=false at the host level is the
// only reliable switch.
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

// ---- Logging (STRG-006, partial) ----
// Replaces the default Microsoft.Extensions.Logging provider with Serilog so that
// SecretFieldsDestructuringPolicy has an effect: the default provider calls ToString() on
// logged objects, and a positional-record ToString() auto-renders every property — including
// Password/ClientSecret/etc. Full STRG-006 wiring (request enrichers, CompactJsonFormatter,
// OTLP) is still pending; this is the minimum surface that makes credential redaction real.
builder.Host.UseSerilog((context, services, loggerConfig) => loggerConfig
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Destructure.With<SecretFieldsDestructuringPolicy>()
    .WriteTo.Console());

// ---- Observability (STRG-007) ----
builder.Services.AddStrgObservability(builder.Configuration);

// ---- Health checks (STRG-008) ----
// Tag is "strg-ready" (NOT the more common "ready") because MassTransit's AddMassTransit
// auto-registers a "masstransit-bus" check tagged "ready" by default, and including it in the
// readiness gate would defeat the EF Outbox pattern: RabbitMQ outages would mark the pod
// not-ready even though business writes still commit and the outbox dispatches the backlog
// when the broker recovers (STRG-061). The "strg-ready" namespace is owned by this app, so
// only checks we explicitly opt in are surfaced through /health/ready.
//
// /health/live runs ZERO checks (Predicate = _ => false) and reflects pure process liveness
// — by design it does not catch deadlocks where the HTTP listener still responds; that's the
// standard tradeoff with K8s liveness probes.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<StrgDbContext>("database", tags: ["strg-ready"])
    .AddCheck<StorageHealthCheck>("storage", tags: ["strg-ready"]);

// ---- Infrastructure ----
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IDriveRepository, DriveRepository>();
builder.Services.AddScoped<IFileRepository, FileRepository>();
builder.Services.AddScoped<IFileVersionRepository, FileVersionRepository>();
builder.Services.AddScoped<IFileKeyRepository, FileKeyRepository>();
builder.Services.AddScoped<ITenantRepository, TenantRepository>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<QuotaService>();
builder.Services.AddScoped<IQuotaService>(sp => sp.GetRequiredService<QuotaService>());
// IQuotaAdminService is the enumeration-oracle-unsafe surface — bind ONLY into authenticated
// admin/diagnostic endpoints via policy-scoped DI. Global registration is acceptable here only
// because consumer-side policy enforcement (AuthPolicies.Admin) gates every call site.
builder.Services.AddScoped<IQuotaAdminService>(sp => sp.GetRequiredService<QuotaService>());
builder.Services.AddScoped<IFileVersionStore, FileVersionStore>();
builder.Services.AddScoped<ITagRepository, TagRepository>();

// STRG-034 — TUS upload pipeline. StrgTusStore creates a fresh StrgDbContext per method (via
// injected DbContextOptions) because tusdotnet's validation pipeline can interleave read calls
// during a single PATCH; sharing one scoped DbContext produces concurrent-operation exceptions.
// TimeProvider.System gives the abandonment-TTL computation a stable clock without coupling to
// DateTimeOffset.UtcNow at the call site.
builder.Services.AddScoped<StrgTusStore>();
builder.Services.Configure<StrgTusOptions>(builder.Configuration.GetSection("Strg:Upload"));
builder.Services.TryAddSingleton(TimeProvider.System);

// ---- WebDAV (STRG-067/069) ----
// Pass IConfiguration so WebDavOptions (PropfindInfinityMaxItems etc.) binds against the live
// appsettings stack. Without configuration the options container has no source to read the
// "WebDav" section from, and the Depth:infinity cap would silently fall back to the default.
builder.Services.AddStrgWebDav(builder.Configuration);

// ---- Validation (STRG-085/086) ----
// Scan Strg.Api for AbstractValidator<T> implementations so self-registration (and future
// validated endpoints) can resolve IValidator<T> from DI without hand-wiring each.
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

// ---- OpenAPI / Swagger (STRG-009) ----
// Spec + UI wiring. The UI is gated by environment in UseStrgOpenApi below; registration of
// the generator itself is always on so the JSON/YAML endpoints work in production.
builder.Services.AddStrgOpenApi();

// ---- CORS + rate limiting + HSTS (STRG-010) ----
// AddStrgCors reads Cors:AllowedOrigins from configuration and fails startup on wildcard
// entries — AllowCredentials() makes '*' spec-invalid and otherwise surfaces only as a
// browser-side CORS rejection at request time, which is hard to trace back to config.
// AddStrgRateLimiting registers an in-memory fixed-window limiter (GlobalLimiter + Auth named
// policy). STRG-117 migrates the limiter store to Redis for multi-node deployments.
builder.Services.AddStrgCors(builder.Configuration);
builder.Services.AddStrgRateLimiting(builder.Configuration);

// HSTS values per STRG-010 AC2: max-age=31536000 (1 year), includeSubDomains, preload NOT set
// (security-review checklist: "HSTS preload is NOT set (risky for new domains)"). The default
// MaxAge is 30 days — too low for the issue's "(max-age=31536000; includeSubDomains)" pin —
// so this override is load-bearing. UseHsts itself remains env-gated below.
builder.Services.Configure<Microsoft.AspNetCore.HttpsPolicy.HstsOptions>(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
    options.Preload = false;
});

// Storage providers (STRG-021/023/024). AddStrgStorageProviders registers the singleton registry
// AND the "local" built-in factory atomically — splitting these into two steps would leave a
// window where IStorageProviderRegistry resolves to an empty registry before startup wires the
// factories in.
builder.Services.AddStrgStorageProviders();

// KEK provider (STRG-026). Singleton because the KEK byte array is immutable after construction
// and all crypto operations on it are allocation-local. Env-var reading happens inside the
// parameterless ctor on the first resolution — failure surfaces at the first encrypted-drive
// write, which is the point where operator feedback is most actionable.
builder.Services.AddSingleton<IKeyProvider, EnvVarKeyProvider>();

// ---- Identity (STRG-014) ----
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddScoped<IUserManager, UserManager>();

builder.Services.AddDbContext<StrgDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("Connection string 'Default' not configured.");
    options.UseNpgsql(connectionString);
    options.UseOpenIddict();
});

// Strg.Application handlers depend on IStrgDbContext (the port) rather than StrgDbContext
// directly. Register the same scoped instance under both.
builder.Services.AddScoped<IStrgDbContext>(sp => sp.GetRequiredService<StrgDbContext>());

// ---- Strg.Application (CQRS foundation) ----
// Wires the source-generated Mediator dispatcher, the five pipeline behaviors, and scans
// Strg.Application for AbstractValidator<T> implementations. Existing Strg.Api validators
// (e.g. RegisterUserRequestValidator) stay registered via the separate AddValidatorsFromAssemblyContaining<Program>
// call below until the matching features are ported into Strg.Application.
builder.Services.AddStrgApplication();

// ---- OpenIddict (STRG-012) ----
builder.Services.AddStrgOpenIddict(builder.Configuration, builder.Environment.IsDevelopment());

// ---- MassTransit + EF Core Outbox (STRG-061) ----
// Send-side outbox: domain events are atomically committed with the business transaction via
// the OutboxMessage/OutboxState tables, then dispatched in the background via RabbitMQ. This
// preserves at-least-once delivery semantics across process crashes between DB commit and broker
// publish — the classic dual-write problem the outbox pattern exists to solve.
builder.Services.AddStrgMassTransit(
    builder.Configuration,
    builder.Environment.IsDevelopment(),
    // Sibling-layer consumers register through this hook because Strg.Infrastructure (where
    // AddStrgMassTransit is defined) cannot reference Strg.GraphQl or Strg.WebDav without
    // inverting the layer dependency. GraphQlSubscriptionPublisher depends on ITopicEventSender;
    // WebDavJwtCacheInvalidationConsumer (STRG-073 Commit 3) depends on IWebDavJwtCache.
    bus =>
    {
        bus.AddConsumer<Strg.GraphQl.Consumers.GraphQlSubscriptionPublisher>();
        bus.AddConsumer<Strg.WebDav.Consumers.WebDavJwtCacheInvalidationConsumer>();
    });

// ---- Authorization policies (STRG-013) ----
builder.Services.AddSingleton<IAuthorizationHandler, ScopeRequirementHandler>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthPolicies.FilesRead,
        p => p.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement("files.read")));
    options.AddPolicy(AuthPolicies.FilesWrite,
        p => p.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement("files.write")));
    options.AddPolicy(AuthPolicies.FilesShare,
        p => p.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement("files.share")));
    options.AddPolicy(AuthPolicies.TagsWrite,
        p => p.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement("tags.write")));
    options.AddPolicy(AuthPolicies.Admin,
        p => p.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement("admin")));
    options.AddPolicy(AuthPolicies.Authenticated,
        p => p.RequireAuthenticatedUser());

    // Require auth everywhere; individual exempt endpoints use [AllowAnonymous]
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// ---- Hosted services ----
// FirstRunInitializationService runs before OpenIddictSeedWorker so that the default tenant +
// SuperAdmin exist before any OpenIddict client records reference them.
builder.Services.AddHostedService<FirstRunInitializationService>();
builder.Services.AddHostedService<OpenIddictSeedWorker>();

// STRG-035 — periodic sweep of abandoned in-flight TUS uploads. Runs every
// StrgTusOptions.UploadCleanupInterval (default 5 min). The job creates its own scope per
// sweep; the scoped HttpTenantContext returns Guid.Empty without an HttpContext, so the job
// disables ONLY the tenant filter via IgnoreQueryFilters([StrgDbContext.TenantFilterName])
// — soft-delete stays enforced. See class summary for the no-quota-release rationale.
builder.Services.AddHostedService<AbandonedUploadCleanupJob>();

// ---- GraphQL (STRG-049) ----
var graphql = builder.Services
    .AddGraphQLServer()
    .AddQueryType(q => q.Name("Query"))
    .AddMutationType(m => m.Name("Mutation"))
    .AddSubscriptionType(s => s.Name("Subscription"))
    .AddType<UserType>()
    .AddType<GraphQlDriveType>()
    .AddType<FileItemType>()
    .AddType<FileVersionType>()
    .AddType<AuditEntryType>()
    .AddType<TagType>()
    .AddType<Strg.GraphQl.Types.FileEventOutputType>()
    .AddType<Strg.GraphQl.Subscriptions.FileSubscriptions>()
    .AddType<RootQueryExtension>()
    .AddType<RootMutationExtension>()
    .AddType<FileItemByIdDataLoader>()
    .AddType<DriveByIdDataLoader>()
    .AddType<UserByIdDataLoader>()
    .AddType<InboxRuleByIdDataLoader>()
    .AddGlobalObjectIdentification()
    .AddFiltering()
    .AddSorting()
    .AddAuthorization()
    .AddErrorFilter<StrgErrorFilter>()
    .AddMaxExecutionDepthRule(10);

if (builder.Environment.IsDevelopment())
{
    graphql.AddInMemorySubscriptions();
}
else
{
    graphql.AddRedisSubscriptions(sp =>
        ConnectionMultiplexer.Connect(
            sp.GetRequiredService<IConfiguration>()["Redis:ConnectionString"]!));
}

if (!builder.Environment.IsDevelopment())
{
    graphql.DisableIntrospection();
}

var app = builder.Build();

// ---- HTTPS redirection + HSTS (STRG-010) ----
// UseHttpsRedirection sits at the top so every downstream middleware (including the /dav branch
// and the OpenAPI spec endpoints) sees the upgraded scheme. In TestServer-hosted integration
// tests no HTTPS port is bound, so the middleware logs a warning and no-ops — existing tests
// continue to pass unchanged. UseHsts is env-gated because (a) the header is browser-cached
// for a year and (b) UseHsts excludes loopback by default, so it would no-op under TestServer
// anyway; the env gate is the canonical pattern. Preload is deliberately NOT set (security
// checklist: "HSTS preload is NOT set (risky for new domains)").
app.UseHttpsRedirection();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseRouting();

// ---- Security headers (STRG-010) ----
// Registered BEFORE the /dav map and UseStrgOpenApi: both short-circuit by path (DAV branch
// has no endpoint metadata, Swashbuckle matches by path) and a header middleware registered
// AFTER them would never run on those responses. SecurityHeadersMiddleware uses
// HttpResponse.OnStarting so headers attach even when a downstream middleware writes the
// response synchronously (Swashbuckle, Results.File, OpenIddict token writes all do this).
app.UseStrgSecurityHeaders();

// STRG-067 — WebDAV branches off BEFORE the app-level UseAuthentication()/UseAuthorization() so
// its middleware terminal (no endpoint metadata) doesn't get caught by the FallbackPolicy
// (RequireAuthenticatedUser) that would reject OPTIONS with 401 and break RFC 4918 §10.1's
// pre-auth capability probe. The branch runs its OWN UseAuthentication() below so /dav traffic
// gets the identity stack; StrgWebDavMiddleware enforces auth explicitly for non-OPTIONS verbs
// via its own IsAuthenticated check (TC-004 pin).
//
// STRG-073 / STRG-074 — the bridge runs FIRST inside the branch, THEN the branch's own
// UseAuthentication() validates the rewritten Bearer header. Do NOT add an outer
// UseAuthentication() BEFORE this Map: AuthenticationHandler<T>._authenticateTask caches the
// result per-request on a handler instance that IAuthenticationHandlerProvider (scoped) shares
// across all UseAuthentication() calls in the request. An outer pre-Map UseAuthentication()
// would run against the original Basic header, cache a NoResult, and the cached result would
// be returned by the branch's UseAuthentication() even after the bridge rewrote the header —
// yielding a silent 401 on every valid Basic Auth request. The STRG-074 integration test
// (WebDavBasicAuthBridgeTests.Correct_basic_credentials_reach_webdav_and_return_207_multistatus)
// is the regression pin for this ordering. The bridge is restricted to the branch (not global)
// so password-grant exchanges never occur on GraphQL, REST, or token endpoints — surfaces that
// have their own Bearer-only auth story.
app.Map("/dav", webdavApp =>
{
    webdavApp.UseMiddleware<BasicAuthJwtBridgeMiddleware>();
    webdavApp.UseAuthentication();
    webdavApp.UseMiddleware<StrgWebDavMiddleware>();
});

// ---- OpenAPI / Swagger (STRG-009) ----
// Registered BEFORE UseAuthentication/UseAuthorization: Swashbuckle's middleware matches by
// path (not endpoint routing) and short-circuits with the spec response, so it never enters
// the FallbackPolicy = RequireAuthenticatedUser gate that would otherwise 401 anonymous
// callers. The Swagger UI is registration-time gated — the prod contract is 404 at the path
// (not a runtime 403) so static UI assets are never served.
//
// UI gate resolution: explicit Strg:OpenApi:UiEnabled config key wins; fallback is
// IsDevelopment(). The config key defends against a classic misconfiguration where a
// Development-branded container ships to prod (rich error pages, dev env var copied from a
// compose file) — flipping only the env would otherwise expose the UI silently. An operator
// must ALSO flip a named key, so the failure mode is "wrong-and-noticed", not "wrong-and-silent".
var openApiUiEnabled = builder.Configuration.GetValue<bool?>("Strg:OpenApi:UiEnabled")
    ?? app.Environment.IsDevelopment();
app.UseStrgOpenApi(openApiUiEnabled);

// ---- CORS (STRG-010) ----
// Placed AFTER the /dav map by deliberate choice. RFC 4918 §10.1 makes OPTIONS the WebDAV
// capability-discovery verb; UseCors's preflight short-circuit would otherwise intercept
// browser-origin OPTIONS requests to /dav and prevent StrgWebDavMiddleware from emitting the
// Allow/DAV headers WebDAV clients require. Browsers don't speak DAV so they don't issue
// cross-origin DAV preflights — keeping CORS scoped after the Map preserves RFC-compliant
// paths for Finder/Explorer while still guarding every REST/GraphQL surface below.
app.UseCors(StrgCorsServiceCollectionExtensions.PolicyName);

// ---- Request logging (STRG-006) ----
// After CORS so the log line reflects the resolved CORS outcome; before auth so unauthenticated
// 401 responses are still captured in request telemetry.
app.UseSerilogRequestLogging();

// ---- Rate limiting (STRG-010) ----
// AFTER UseRouting (endpoint-specific RequireRateLimiting metadata needs the matched endpoint),
// AFTER UseStrgOpenApi (don't throttle anonymous spec fetches), and BEFORE UseAuthentication —
// the security-review checklist explicitly requires "rate limit before auth (prevents auth
// bypass via rate-limit exploit)". Health checks and /metrics chain DisableRateLimiting() on
// their endpoint mappings below to bypass the limiter.
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.UseWebSockets();

// AllowAnonymous is required because the fallback authorization policy (RequireAuthenticatedUser)
// would otherwise reject unauthenticated Prometheus scrape requests with 401. DisableRateLimiting
// is required per STRG-010 AC: "/metrics endpoint bypass auth and rate limiting".
app.MapPrometheusScrapingEndpoint("/metrics")
    .AllowAnonymous()
    .DisableRateLimiting();

// ---- Health endpoints (STRG-008 + STRG-010) ----
// .AllowAnonymous() is mandatory: the FallbackPolicy above is RequireAuthenticatedUser and
// would otherwise 401 every K8s probe (probes cannot present credentials). /health/live has
// zero checks → 200 as long as the process serves HTTP. /health/ready runs the "ready"-tagged
// checks; SafeHealthCheckResponseWriter emits a minimal JSON envelope that NEVER serializes
// the captured Exception (Npgsql exception messages embed the database host/username and would
// leak via the default UIResponseWriter — see SafeHealthCheckResponseWriter XML docs).
//
// .DisableRateLimiting() satisfies STRG-010 AC "Health check endpoints bypass rate limiting" —
// K8s probes burst at a steady cadence and are explicitly exempt from the global limiter so
// the probe cadence itself cannot exhaust the pod's budget for real traffic.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
}).AllowAnonymous().DisableRateLimiting();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("strg-ready"),
    ResponseWriter = SafeHealthCheckResponseWriter.WriteAsync,
}).AllowAnonymous().DisableRateLimiting();

app.MapGraphQL();
app.MapTokenEndpoints();
app.MapUserInfoEndpoints();
app.MapDriveEndpoints();
app.MapUserRegistrationEndpoints();

// STRG-034 — TUS upload endpoint. Mapped after UseAuthentication/UseAuthorization (line 352-353)
// so HttpContext.User is populated before OnAuthorizeAsync runs. .RequireAuthorization() is
// applied inside MapStrgTusUpload (defence-in-depth alongside the OnAuthorize event hook), and
// .DisableRateLimiting() exempts byte-rate uploads from the per-request global limiter (Phase-2
// design memory: "TUS after auth + excluded from rate limiting").
app.MapStrgTusUpload();

app.Run();
