using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Serilog;
using StackExchange.Redis;
using Strg.Api.Auth;
using Strg.Api.Endpoints;
using Strg.Core.Auditing;
using Strg.Core.Domain;
using Strg.Core.Identity;
using Strg.Core.Services;
using Strg.Core.Storage;
using Strg.Infrastructure.Auditing;
using Strg.GraphQL.DataLoaders;
using Strg.GraphQL.Errors;
using Strg.GraphQL.Mutations;
using Strg.GraphQL.Queries;
using Strg.GraphQL.Types;
using GraphQLDriveType = Strg.GraphQL.Types.DriveType;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Identity;
using Strg.Infrastructure.Observability;
using Strg.Infrastructure.Services;
using Strg.Infrastructure.Storage;
using Strg.Infrastructure.Versioning;

var builder = WebApplication.CreateBuilder(args);

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

// ---- Infrastructure ----
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();
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

// ---- Validation (STRG-085/086) ----
// Scan Strg.Api for AbstractValidator<T> implementations so self-registration (and future
// validated endpoints) can resolve IValidator<T> from DI without hand-wiring each.
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

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

// ---- OpenIddict (STRG-012) ----
builder.Services.AddStrgOpenIddict(builder.Configuration, builder.Environment.IsDevelopment());

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

// ---- MVC / Controllers (token + userinfo endpoints) ----
builder.Services.AddControllers();

// ---- GraphQL (STRG-049) ----
var graphql = builder.Services
    .AddGraphQLServer()
    .AddQueryType(q => q.Name("Query"))
    .AddMutationType(m => m.Name("Mutation"))
    .AddSubscriptionType(s => s.Name("Subscription"))
    .AddType<UserType>()
    .AddType<GraphQLDriveType>()
    .AddType<FileItemType>()
    .AddType<FileVersionType>()
    .AddType<AuditEntryType>()
    .AddType<TagType>()
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

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.UseWebSockets();
app.MapGraphQL("/graphql");
app.MapControllers();
app.MapDriveEndpoints();
app.MapUserRegistrationEndpoints();

app.Run();
