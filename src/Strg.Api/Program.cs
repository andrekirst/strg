using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Strg.Api.Auth;
using Strg.Api.Endpoints;
using Strg.Core.Domain;
using Strg.GraphQL.DataLoaders;
using Strg.GraphQL.Errors;
using Strg.GraphQL.Mutations;
using Strg.GraphQL.Queries;
using Strg.GraphQL.Types;
using GraphQLDriveType = Strg.GraphQL.Types.DriveType;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Identity;

var builder = WebApplication.CreateBuilder(args);

// ---- Infrastructure ----
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IDriveRepository, DriveRepository>();

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
    graphql.AddInMemorySubscriptions();
else
    graphql.AddRedisSubscriptions(sp =>
        ConnectionMultiplexer.Connect(
            sp.GetRequiredService<IConfiguration>()["Redis:ConnectionString"]!));

if (!builder.Environment.IsDevelopment())
    graphql.DisableIntrospection();

var app = builder.Build();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.UseWebSockets();
app.MapGraphQL("/graphql");
app.MapControllers();
app.MapDriveEndpoints();

app.Run();
