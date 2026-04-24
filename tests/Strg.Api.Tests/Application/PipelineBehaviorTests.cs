using FluentAssertions;
using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Strg.Application.Abstractions;
using Strg.Application.Auditing;
using Strg.Application.Behaviors;
using Strg.Core;
using Strg.Core.Auditing;
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
    public async Task AuditBehavior_skips_write_when_command_is_not_audited()
    {
        var auditScope = Substitute.For<IAuditScope>();
        auditScope.IsPopulated.Returns(true);
        auditScope.BuildEntry().Returns(SampleAuditEntry());
        var auditService = Substitute.For<IAuditService>();
        var behavior = new AuditBehavior<TenantScopedDummy, Result<string>>(
            auditScope, auditService, NullLogger<AuditBehavior<TenantScopedDummy, Result<string>>>.Instance);

        var result = await behavior.Handle(
            new TenantScopedDummy(),
            (_, _) => ValueTask.FromResult(Result<string>.Success("ok")),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await auditService.DidNotReceive().LogAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AuditBehavior_skips_write_when_scope_is_not_populated()
    {
        var auditScope = Substitute.For<IAuditScope>();
        auditScope.IsPopulated.Returns(false);
        var auditService = Substitute.For<IAuditService>();
        var behavior = new AuditBehavior<AuditedDummy, Result<string>>(
            auditScope, auditService, NullLogger<AuditBehavior<AuditedDummy, Result<string>>>.Instance);

        var result = await behavior.Handle(
            new AuditedDummy(),
            (_, _) => ValueTask.FromResult(Result<string>.Success("ok")),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await auditService.DidNotReceive().LogAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AuditBehavior_skips_write_on_result_failure()
    {
        var auditScope = Substitute.For<IAuditScope>();
        auditScope.IsPopulated.Returns(true);
        auditScope.BuildEntry().Returns(SampleAuditEntry());
        var auditService = Substitute.For<IAuditService>();
        var behavior = new AuditBehavior<AuditedDummy, Result<string>>(
            auditScope, auditService, NullLogger<AuditBehavior<AuditedDummy, Result<string>>>.Instance);

        var result = await behavior.Handle(
            new AuditedDummy(),
            (_, _) => ValueTask.FromResult(Result<string>.Failure("SomeError", "nope")),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await auditService.DidNotReceive().LogAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AuditBehavior_writes_entry_when_audited_scope_populated_and_success()
    {
        var entry = SampleAuditEntry();
        var auditScope = Substitute.For<IAuditScope>();
        auditScope.IsPopulated.Returns(true);
        auditScope.BuildEntry().Returns(entry);
        var auditService = Substitute.For<IAuditService>();
        var behavior = new AuditBehavior<AuditedDummy, Result<string>>(
            auditScope, auditService, NullLogger<AuditBehavior<AuditedDummy, Result<string>>>.Instance);

        var result = await behavior.Handle(
            new AuditedDummy(),
            (_, _) => ValueTask.FromResult(Result<string>.Success("ok")),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await auditService.Received(1).LogAsync(entry, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AuditBehavior_writes_entry_when_response_is_not_a_result_shape()
    {
        var entry = SampleAuditEntry();
        var auditScope = Substitute.For<IAuditScope>();
        auditScope.IsPopulated.Returns(true);
        auditScope.BuildEntry().Returns(entry);
        var auditService = Substitute.For<IAuditService>();
        var behavior = new AuditBehavior<NonResultAuditedDummy, int>(
            auditScope, auditService, NullLogger<AuditBehavior<NonResultAuditedDummy, int>>.Instance);

        var value = await behavior.Handle(
            new NonResultAuditedDummy(),
            (_, _) => ValueTask.FromResult(42),
            CancellationToken.None);

        value.Should().Be(42);
        await auditService.Received(1).LogAsync(entry, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AuditBehavior_swallows_non_cancellation_exception_from_audit_service()
    {
        var auditScope = Substitute.For<IAuditScope>();
        auditScope.IsPopulated.Returns(true);
        auditScope.BuildEntry().Returns(SampleAuditEntry());
        var auditService = Substitute.For<IAuditService>();
        auditService
            .LogAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("audit store unreachable"));
        var behavior = new AuditBehavior<AuditedDummy, Result<string>>(
            auditScope, auditService, NullLogger<AuditBehavior<AuditedDummy, Result<string>>>.Instance);

        var result = await behavior.Handle(
            new AuditedDummy(),
            (_, _) => ValueTask.FromResult(Result<string>.Success("ok")),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue("primary op must survive an audit-store outage");
        result.Value.Should().Be("ok");
    }

    [Fact]
    public async Task AuditBehavior_rethrows_operation_canceled_from_audit_service()
    {
        var auditScope = Substitute.For<IAuditScope>();
        auditScope.IsPopulated.Returns(true);
        auditScope.BuildEntry().Returns(SampleAuditEntry());
        var auditService = Substitute.For<IAuditService>();
        auditService
            .LogAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new OperationCanceledException());
        var behavior = new AuditBehavior<AuditedDummy, Result<string>>(
            auditScope, auditService, NullLogger<AuditBehavior<AuditedDummy, Result<string>>>.Instance);

        var act = async () => await behavior.Handle(
            new AuditedDummy(),
            (_, _) => ValueTask.FromResult(Result<string>.Success("ok")),
            CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AuditBehavior_resets_scope_after_successful_handler()
    {
        var auditScope = Substitute.For<IAuditScope>();
        auditScope.IsPopulated.Returns(true);
        auditScope.BuildEntry().Returns(SampleAuditEntry());
        var auditService = Substitute.For<IAuditService>();
        var behavior = new AuditBehavior<AuditedDummy, Result<string>>(
            auditScope, auditService, NullLogger<AuditBehavior<AuditedDummy, Result<string>>>.Instance);

        await behavior.Handle(
            new AuditedDummy(),
            (_, _) => ValueTask.FromResult(Result<string>.Success("ok")),
            CancellationToken.None);

        auditScope.Received(1).Reset();
    }

    [Fact]
    public async Task AuditBehavior_resets_scope_when_handler_throws()
    {
        var auditScope = Substitute.For<IAuditScope>();
        auditScope.IsPopulated.Returns(true);
        var auditService = Substitute.For<IAuditService>();
        var behavior = new AuditBehavior<AuditedDummy, Result<string>>(
            auditScope, auditService, NullLogger<AuditBehavior<AuditedDummy, Result<string>>>.Instance);

        var act = async () => await behavior.Handle(
            new AuditedDummy(),
            (_, _) => throw new InvalidOperationException("handler crashed"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        auditScope.Received(1).Reset();
    }

    private static AuditEntry SampleAuditEntry() => new()
    {
        TenantId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Action = "test.action",
        ResourceType = "TestResource",
        ResourceId = Guid.NewGuid(),
        Details = "sample",
    };

    private sealed record TenantScopedDummy : ICommand<Result<string>>, ITenantScopedCommand;

    private sealed record AuditedDummy : ICommand<Result<string>>, IAuditedCommand;

    private sealed record NonResultAuditedDummy : ICommand<int>, IAuditedCommand;
}
