using FluentAssertions;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Strg.Application.Abstractions;
using Strg.Application.DependencyInjection;
using Strg.Application.Features.Ping;
using Strg.Core.Auditing;
using Strg.Core.Domain;
using Xunit;

namespace Strg.Api.Tests.Application;

/// <summary>
/// Phase 1 foundation canary: exercises Strg.Application end-to-end through a ServiceCollection
/// assembled the same way Program.cs does, minus the concrete DbContext and HttpTenantContext.
/// If AddStrgApplication, the source-generated IMediator, and the pipeline behaviors all wire
/// correctly, PingCommand round-trips through every behavior into PingHandler and back. A
/// failing validator proves the pipeline short-circuits to Result.Failure rather than invoking
/// the handler — the contract ValidationBehavior is built around.
/// </summary>
public sealed class PingCommandTests
{
    [Fact]
    public async Task Send_valid_PingCommand_returns_success_response()
    {
        var mediator = BuildMediator();

        var result = await mediator.Send(new PingCommand("hello"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Echo.Should().Be("pong: hello");
    }

    [Fact]
    public async Task Send_empty_message_short_circuits_to_validation_failure()
    {
        var mediator = BuildMediator();

        var result = await mediator.Send(new PingCommand(string.Empty));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("ValidationError");
        result.ErrorMessage.Should().Contain("Message");
    }

    [Fact]
    public async Task Send_oversized_message_short_circuits_to_validation_failure()
    {
        var mediator = BuildMediator();

        var result = await mediator.Send(new PingCommand(new string('a', 201)));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("ValidationError");
    }

    private static IMediator BuildMediator()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // PingCommand is not ITenantScopedCommand, so TenantScopeBehavior never inspects the
        // tenant id. The substitute only needs to be resolvable to satisfy the behavior's ctor.
        services.AddScoped<ITenantContext>(_ => Substitute.For<ITenantContext>());

        // Same reasoning for IStrgDbContext: PingCommand is not ITransactionalCommand, so
        // TransactionBehavior never touches Database. The substitute's properties return nulls
        // by default, which is never observed along the PingCommand code path.
        services.AddScoped<IStrgDbContext>(_ => Substitute.For<IStrgDbContext>());

        // Same reasoning for ICurrentUser: PingCommand is not IAuditedCommand, so the AuditScope
        // never calls BuildEntry. ICurrentUser is only resolved if a populated entry needs to be
        // composed; the substitute satisfies AuditScope's ctor activation.
        services.AddScoped<ICurrentUser>(_ => Substitute.For<ICurrentUser>());

        // Same reasoning for IAuditService: PingCommand is not IAuditedCommand, so AuditBehavior
        // never invokes LogAsync. The substitute satisfies the behavior's ctor activation.
        services.AddScoped<IAuditService>(_ => Substitute.For<IAuditService>());

        services.AddStrgApplication();

        return services.BuildServiceProvider().GetRequiredService<IMediator>();
    }
}
