using System.Text.Json;
using HotChocolate.Authorization;
using HotChocolate.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Domain;
using Strg.GraphQl.Mutations;
using Strg.GraphQl.Mutations.Storage;
using Strg.GraphQl.Tests.Helpers;
using Strg.Infrastructure.Data;
using Xunit;
using GraphQLDriveType = Strg.GraphQl.Types.DriveType;

namespace Strg.GraphQl.Tests.Mutations;

[Collection("database")]
public class DriveMutationsTests
{
    private static readonly TestTenantContext SharedTenantCtx = TestTenantContext.Shared;

    private Task<TestExecutor> CreateExecutorAsync(Guid tenantId, string dbName) =>
        GraphQlTestFixture.CreateExecutorAsync(
            configureServices: services =>
            {
                services.AddSingleton<ITenantContext>(SharedTenantCtx);
                services.AddDbContext<StrgDbContext>(o => o.UseInMemoryDatabase(dbName));
                services.AddStrgApplicationForTests();
            },
            configureSchema: b =>
            {
                b.AddAuthorization()
                 .AddMutationType(m => m.Name("Mutation"))
                 .AddType<RootMutationExtension>()
                 .AddType<StorageMutations>()
                 .AddType<DriveMutations>()
                 .AddType<GraphQLDriveType>()
                 .AddGlobalObjectIdentification();
                b.Services.AddSingleton<IAuthorizationHandler, AllowAllAuthorizationHandler>();
            },
            globalState: new Dictionary<string, object?> { ["tenantId"] = tenantId });

    [Fact]
    public async Task CreateDrive_InvalidName_ReturnsValidationError()
    {
        var tenantId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;

        var executor = await CreateExecutorAsync(tenantId, Guid.NewGuid().ToString());

        var result = (IOperationResult)await executor.ExecuteAsync("""
            mutation {
              storage {
                createDrive(input: { name: "My Invalid Drive!", providerType: "local", providerConfig: "{}", isEncrypted: false }) {
                  drive { id }
                  errors { code field }
                }
              }
            }
            """);

        var json = result.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("data", out var data), $"no data: {json}");
        var errorsEl = data.GetProperty("storage").GetProperty("createDrive").GetProperty("errors");
        Assert.Equal(JsonValueKind.Array, errorsEl.ValueKind);
        var errors = errorsEl.EnumerateArray().ToList();
        Assert.NotEmpty(errors);
        Assert.Equal("VALIDATION_ERROR", errors[0].GetProperty("code").GetString());
        Assert.Equal("name", errors[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task CreateDrive_ProviderConfigOver8192Chars_ReturnsValidationError()
    {
        var tenantId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;

        var executor = await CreateExecutorAsync(tenantId, Guid.NewGuid().ToString());

        // 8193 x's — one over the service-layer guard (and the DB varchar(8192) backstop).
        var oversized = new string('x', 8193);
        var result = (IOperationResult)await executor.ExecuteAsync($$"""
            mutation {
              storage {
                createDrive(input: { name: "my-drive", providerType: "local", providerConfig: "{{oversized}}", isEncrypted: false }) {
                  drive { id }
                  errors { code field }
                }
              }
            }
            """);

        var json = result.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("data", out var data), $"no data: {json}");
        var errorsEl = data.GetProperty("storage").GetProperty("createDrive").GetProperty("errors");
        Assert.Equal(JsonValueKind.Array, errorsEl.ValueKind);
        var errors = errorsEl.EnumerateArray().ToList();
        Assert.NotEmpty(errors);
        Assert.Equal("VALIDATION_ERROR", errors[0].GetProperty("code").GetString());
        Assert.Equal("providerConfig", errors[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task CreateDrive_ValidInput_ReturnsDrive()
    {
        var tenantId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;

        var executor = await CreateExecutorAsync(tenantId, Guid.NewGuid().ToString());

        var result = (IOperationResult)await executor.ExecuteAsync("""
            mutation {
              storage {
                createDrive(input: { name: "my-drive", providerType: "local", providerConfig: "{}", isEncrypted: false }) {
                  drive { id name }
                  errors { code }
                }
              }
            }
            """);

        var json = result.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("data", out var data), $"no data: {json}");
        var driveEl = data.GetProperty("storage").GetProperty("createDrive").GetProperty("drive");
        Assert.Equal(JsonValueKind.Object, driveEl.ValueKind);
        Assert.Equal("my-drive", driveEl.GetProperty("name").GetString());
    }

    /// <summary>
    /// STRG-300 — verifies the <c>setDefaultDrive</c> mutation surfaces the picked drive in the
    /// payload and returns no errors on the happy path. Drives the wire contract end-to-end:
    /// input shape, payload shape, auth (the [Authorize] attribute is satisfied by
    /// AllowAllAuthorizationHandler), and the new <c>isDefault</c> field exposure.
    /// </summary>
    [Fact]
    public async Task SetDefaultDrive_ValidDriveId_ReturnsDrive()
    {
        var tenantId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;
        var dbName = Guid.NewGuid().ToString();

        var executor = await CreateExecutorAsync(tenantId, dbName);

        // Seed a drive via the same in-memory DB the mutation reads from.
        var driveId = Guid.NewGuid();
        await using (var scope = executor.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            db.Drives.Add(new Drive
            {
                Id = driveId,
                TenantId = tenantId,
                Name = "the-drive",
                ProviderType = "local",
                ProviderConfig = "{}",
                EncryptionEnabled = false,
                IsDefault = true,
            });
            await db.SaveChangesAsync();
        }

        var result = (IOperationResult)await executor.ExecuteAsync($$"""
            mutation {
              storage {
                setDefaultDrive(input: { driveId: "{{driveId}}" }) {
                  drive { id name isDefault }
                  errors { code }
                }
              }
            }
            """);

        var json = result.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("data", out var data), $"no data: {json}");
        var setDefaultEl = data.GetProperty("storage").GetProperty("setDefaultDrive");
        var driveEl = setDefaultEl.GetProperty("drive");
        Assert.Equal(JsonValueKind.Object, driveEl.ValueKind);
        Assert.Equal("the-drive", driveEl.GetProperty("name").GetString());
        Assert.True(driveEl.GetProperty("isDefault").GetBoolean(), "the GraphQL DriveType must expose isDefault");

        var errorsEl = setDefaultEl.GetProperty("errors");
        Assert.True(errorsEl.ValueKind == JsonValueKind.Null || !errorsEl.EnumerateArray().Any(),
            $"happy path must return no errors; got {errorsEl}");
    }

    /// <summary>
    /// STRG-300 TC-005 — cross-tenant DriveId. The handler throws <c>NotFoundException</c>
    /// (mapped by <c>StrgErrorFilter</c> to the top-level <c>NOT_FOUND</c> error rather than to
    /// the payload's <c>errors[]</c>). Pins the global tenant filter at the GraphQL surface.
    /// </summary>
    [Fact]
    public async Task SetDefaultDrive_CrossTenantDriveId_ReturnsNotFoundError()
    {
        var tenantId = Guid.NewGuid();
        var foreignTenantId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;
        var dbName = Guid.NewGuid().ToString();

        var executor = await CreateExecutorAsync(tenantId, dbName);

        // Seed a drive that belongs to a foreign tenant. The current tenant's filter must hide it.
        var foreignDriveId = Guid.NewGuid();
        await using (var scope = executor.Services.CreateAsyncScope())
        {
            // Switch the shared context to the foreign tenant for the seed insert, then flip back.
            SharedTenantCtx.TenantId = foreignTenantId;
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
                db.Drives.Add(new Drive
                {
                    Id = foreignDriveId,
                    TenantId = foreignTenantId,
                    Name = "foreign-drive",
                    ProviderType = "local",
                    ProviderConfig = "{}",
                    EncryptionEnabled = false,
                });
                await db.SaveChangesAsync();
            }
            finally
            {
                SharedTenantCtx.TenantId = tenantId;
            }
        }

        var result = (IOperationResult)await executor.ExecuteAsync($$"""
            mutation {
              storage {
                setDefaultDrive(input: { driveId: "{{foreignDriveId}}" }) {
                  drive { id }
                  errors { code }
                }
              }
            }
            """);

        var json = result.ToJson();
        using var doc = JsonDocument.Parse(json);
        // NotFoundException surfaces as a top-level GraphQL error (NOT in the payload's errors[]).
        Assert.True(doc.RootElement.TryGetProperty("errors", out var errorsEl), $"expected top-level errors: {json}");
        var topErrors = errorsEl.EnumerateArray().ToList();
        Assert.NotEmpty(topErrors);
        Assert.Equal("NOT_FOUND", topErrors[0].GetProperty("extensions").GetProperty("code").GetString());
    }
}
