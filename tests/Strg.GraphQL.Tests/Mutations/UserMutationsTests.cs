using System.Text.Json;
using HotChocolate.Authorization;
using HotChocolate.Execution;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Domain;
using Strg.Core.Identity;
using Strg.Core.Services;
using Strg.GraphQL.Mutations;
using Strg.GraphQL.Mutations.User;
using Strg.GraphQL.Tests.Helpers;
using Strg.GraphQL.Types;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Identity;
using Strg.Infrastructure.Services;
using Xunit;

namespace Strg.GraphQL.Tests.Mutations;

// No-op IPublishEndpoint for tests exercising GraphQL mutations that traverse UserManager's
// pre-save publish. The outbox → consumer wire is covered by WebDavJwtCacheInvalidationConsumerTests
// with a real MassTransit pipeline; this surface only needs the DI slot filled so UserManager can
// be constructed and ChangePasswordAsync can run its length gate BEFORE the verify step.
internal sealed class NoopPublishEndpoint : IPublishEndpoint
{
    public Task Publish<T>(T message, CancellationToken cancellationToken = default) where T : class
        => Task.CompletedTask;
    public Task Publish<T>(T message, IPipe<PublishContext<T>> pipe, CancellationToken cancellationToken = default) where T : class
        => Task.CompletedTask;
    public Task Publish<T>(T message, IPipe<PublishContext> pipe, CancellationToken cancellationToken = default) where T : class
        => Task.CompletedTask;
    public Task Publish(object message, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task Publish(object message, IPipe<PublishContext> pipe, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task Publish(object message, Type messageType, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task Publish(object message, Type messageType, IPipe<PublishContext> pipe, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task Publish<T>(object values, CancellationToken cancellationToken = default) where T : class
        => Task.CompletedTask;
    public Task Publish<T>(object values, IPipe<PublishContext<T>> pipe, CancellationToken cancellationToken = default) where T : class
        => Task.CompletedTask;
    public Task Publish<T>(object values, IPipe<PublishContext> pipe, CancellationToken cancellationToken = default) where T : class
        => Task.CompletedTask;
    public ConnectHandle ConnectPublishObserver(IPublishObserver observer)
        => throw new NotSupportedException();
}

[Collection("database")]
public class UserMutationsTests
{
    private static readonly TestTenantContext SharedTenantCtx = TestTenantContext.Shared;

    private Task<TestExecutor> CreateExecutorAsync(Guid tenantId, Guid userId, string dbName) =>
        GraphQLTestFixture.CreateExecutorAsync(
            configureServices: services =>
            {
                services.AddSingleton<ITenantContext>(SharedTenantCtx);
                services.AddDbContext<StrgDbContext>(o => o.UseInMemoryDatabase(dbName));
                services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
                services.AddSingleton<IPublishEndpoint, NoopPublishEndpoint>();
                services.AddScoped<IUserRepository, UserRepository>();
                services.AddScoped<IUserManager, UserManager>();
            },
            configureSchema: b =>
            {
                b.AddAuthorization()
                 .AddMutationType(m => m.Name("Mutation"))
                 .AddType<RootMutationExtension>()
                 .AddType<UserMutations>()
                 .AddType<UserMutationHandlers>()
                 .AddType<UserType>()
                 .AddGlobalObjectIdentification();
                b.Services.AddSingleton<IAuthorizationHandler, AllowAllAuthorizationHandler>();
            },
            globalState: new Dictionary<string, object?> { ["tenantId"] = tenantId, ["userId"] = userId });

    [Fact]
    public async Task UpdateProfile_DisplayNameTooLong_ReturnsValidationError()
    {
        var tenantId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;

        var executor = await CreateExecutorAsync(tenantId, Guid.NewGuid(), Guid.NewGuid().ToString());

        var longName = new string('x', 256);
        var result = (IOperationResult)await executor.ExecuteAsync($$"""
            mutation {
              user {
                updateProfile(input: { displayName: "{{longName}}" }) {
                  user { displayName }
                  errors { code field }
                }
              }
            }
            """);

        var json = result.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("data", out var data), $"no data: {json}");
        var errorsEl = data.GetProperty("user").GetProperty("updateProfile").GetProperty("errors");
        var errors = errorsEl.EnumerateArray().ToList();
        Assert.NotEmpty(errors);
        Assert.Equal("VALIDATION_ERROR", errors[0].GetProperty("code").GetString());
        Assert.Equal("displayName", errors[0].GetProperty("field").GetString());
    }

    // STRG-073 MED-5 regression. Before the resolver was rewired to delegate to
    // IUserManager.ChangePasswordAsync, UserMutationHandlers.ChangePasswordAsync re-implemented
    // verify+hash+publish inline and SKIPPED the UserManagerErrors.MinimumPasswordLength gate
    // (12 chars). That let an authenticated user set a 1-char password through GraphQL while the
    // REST/admin paths refused the same input via UserManager. This test pins the gate at the
    // GraphQL boundary so a future refactor that reintroduces inline verify+hash cannot silently
    // drop the policy again — the mutation must surface VALIDATION_ERROR on newPassword without
    // persisting any change.
    [Fact]
    public async Task ChangePassword_NewPasswordBelowMinimumLength_ReturnsValidationError()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;
        var dbName = Guid.NewGuid().ToString();
        const string currentPassword = "correct-horse-battery-staple";

        var executor = await CreateExecutorAsync(tenantId, userId, dbName);

        using (var scope = executor.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            db.Tenants.Add(new Tenant { Id = tenantId, Name = $"test-{tenantId:N}" });
            db.Users.Add(new User
            {
                Id = userId,
                TenantId = tenantId,
                Email = $"alice-{userId:N}@example.test",
                DisplayName = "Alice",
                PasswordHash = hasher.Hash(currentPassword),
            });
            await db.SaveChangesAsync();
        }

        var result = (IOperationResult)await executor.ExecuteAsync($$"""
            mutation {
              user {
                changePassword(input: { currentPassword: "{{currentPassword}}", newPassword: "x" }) {
                  user { id }
                  errors { code field }
                }
              }
            }
            """);

        var json = result.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("data", out var data), $"no data: {json}");
        var payload = data.GetProperty("user").GetProperty("changePassword");
        var errors = payload.GetProperty("errors").EnumerateArray().ToList();
        Assert.NotEmpty(errors);
        Assert.Equal("VALIDATION_ERROR", errors[0].GetProperty("code").GetString());
        Assert.Equal("newPassword", errors[0].GetProperty("field").GetString());

        // Payload.user must be null on failure (the wire contract for UpdateProfilePayload /
        // ChangePasswordPayload). Asserts that the handler returned the error path, not the
        // success path.
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("user").ValueKind);

        // Verify on the DB directly that the PasswordHash was NOT rotated — the gate must refuse
        // BEFORE the new hash is computed and persisted. Without this assertion the test could
        // pass against a broken implementation that returned VALIDATION_ERROR but still wrote the
        // shorter password, re-introducing the bypass silently.
        using var verifyScope = executor.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var verifyHasher = verifyScope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var persisted = await verifyDb.Users.FirstOrDefaultAsync(u => u.Id == userId);
        Assert.NotNull(persisted);
        Assert.True(verifyHasher.Verify(currentPassword, persisted!.PasswordHash),
            "original password must still verify — the length-gate must reject BEFORE persistence");
    }
}
