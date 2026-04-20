using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Strg.Api.Auth;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Identity;

var builder = WebApplication.CreateBuilder(args);

// ---- Infrastructure ----
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();
builder.Services.AddScoped<IUserRepository, UserRepository>();

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

var app = builder.Build();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
