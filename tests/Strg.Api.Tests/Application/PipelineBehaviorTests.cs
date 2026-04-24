using FluentAssertions;
using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Strg.Application.Abstractions;
using Strg.Application.Behaviors;
using Strg.Core;
using Strg.Core.Domain;
using Xunit;

namespace Strg.Api.Tests.Application;

/// <summary>
/// Exercises the ENABLED branch of each marker-gated pipeline behavior. The PingCommand smoke
/// tests only cover the skip paths (PingCommand implements none of the markers), so a typo in a
/// marker check — e.g. <c>is ITenantScopedCommand</c> vs <c>is not ITenantScopedCommand</c> —
/// would not surface until Phase 2's first real feature. These direct-instantiation tests close
/// that gap cheaply: no Mediator or DI involved, the behavior's next() delegate is supplied
/// inline.
/// </summary>
public sealed class PipelineBehaviorTests
{
    [Fact]
    public async Task TenantScopeBehavior_rejects_tenant_scoped_command_when_tenant_is_empty()
    {
        var currentTenant = Substitute.For<ITenantContext>();
        currentTenant.TenantId.Returns(Guid.Empty);

        var behavior = new TenantScopeBehavior<TenantScopedDummy, Result<string>>(currentTenant);

        var act = async () => await behavior.Handle(
            new TenantScopedDummy(),
            (_, _) => ValueTask.FromResult(Result<string>.Success("never-reached")),
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*tenant-scoped*");
    }

    [Fact]
    public async Task TenantScopeBehavior_passes_tenant_scoped_command_when_tenant_is_bound()
    {
        var currentTenant = Substitute.For<ITenantContext>();
        currentTenant.TenantId.Returns(Guid.NewGuid());

        var behavior = new TenantScopeBehavior<TenantScopedDummy, Result<string>>(currentTenant);

        var result = await behavior.Handle(
            new TenantScopedDummy(),
            (_, _) => ValueTask.FromResult(Result<string>.Success("ok")),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("ok");
    }

    [Fact]
    public async Task AuditBehavior_invokes_handler_and_completes_for_audited_command()
    {
        var logger = NullLogger<AuditBehavior<AuditedDummy, Result<string>>>.Instance;
        var behavior = new AuditBehavior<AuditedDummy, Result<string>>(logger);
        var nextCalled = false;

        var result = await behavior.Handle(
            new AuditedDummy(),
            (_, _) =>
            {
                nextCalled = true;
                return ValueTask.FromResult(Result<string>.Success("ok"));
            },
            CancellationToken.None);

        nextCalled.Should().BeTrue("audit runs post-success — next() must complete first");
        result.IsSuccess.Should().BeTrue();
    }

    private sealed record TenantScopedDummy : ICommand<Result<string>>, ITenantScopedCommand;

    private sealed record AuditedDummy : ICommand<Result<string>>, IAuditedCommand;
}
