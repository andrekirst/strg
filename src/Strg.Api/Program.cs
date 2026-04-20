using Strg.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();

builder.Services.AddDbContext<StrgDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("Connection string 'Default' not configured.");
    options.UseNpgsql(connectionString);
});

var app = builder.Build();
app.Run();
