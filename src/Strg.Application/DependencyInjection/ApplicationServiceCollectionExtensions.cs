using FluentValidation;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Strg.Application.Auditing;
using Strg.Application.Behaviors;

namespace Strg.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the source-generated Mediator dispatcher, all pipeline behaviors, and every
    /// <see cref="AbstractValidator{T}"/> declared in the Strg.Application assembly. Call once
    /// from Strg.Api's Program.cs after the DbContext is registered and before the HTTP pipeline
    /// is built. Callers still need to register adapters (<see cref="Abstractions.IStrgDbContext"/>,
    /// <see cref="Strg.Core.Domain.ITenantContext"/>) separately because their concrete types live
    /// in Strg.Infrastructure.
    /// </summary>
    public static IServiceCollection AddStrgApplication(this IServiceCollection services)
    {
        services.AddMediator(options =>
        {
            options.ServiceLifetime = ServiceLifetime.Scoped;
            options.Namespace = "Strg.Application";
        });

        // Pipeline order is outer → inner. Logging wraps everything so timings cover the full
        // pipeline. Validation runs before TenantScope so an unauthenticated caller submitting
        // a malformed body still gets a validation error (the richer signal) rather than a
        // tenant error. Audit runs last before the handler so the handler's outcome dictates
        // whether to audit. Transaction wraps the handler tightly so nested SaveChangesAsync
        // calls commit together.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TenantScopeBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(AuditBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

        services.AddScoped<IAuditScope, AuditScope>();

        services.AddValidatorsFromAssembly(typeof(ApplicationServiceCollectionExtensions).Assembly);

        return services;
    }
}
