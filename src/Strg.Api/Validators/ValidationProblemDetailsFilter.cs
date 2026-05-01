using FluentValidation;

namespace Strg.Api.Validators;

/// <summary>
/// STRG-085 — generic <see cref="IEndpointFilter"/> that runs the registered
/// <see cref="IValidator{TRequest}"/> against the matching argument in the endpoint's parameter
/// list and surfaces failures as RFC 7807 <c>ValidationProblemDetails</c> (HTTP 400). Wire it via
/// <c>.AddEndpointFilter&lt;ValidationProblemDetailsFilter&lt;TRequest&gt;&gt;()</c> on minimal-API
/// routes whose body type is <typeparamref name="TRequest"/>.
///
/// <para><b>Why a filter instead of <c>AddFluentValidationAutoValidation()</c>.</b> The package's
/// auto-validation hooks into MVC's <c>IModelBinder</c> pipeline; minimal APIs (which this app
/// uses end-to-end) bind via <c>RequestDelegateFactory</c> and never invoke the MVC pipeline. The
/// auto-validator call is still registered in <c>Program.cs</c> for forward compatibility if any
/// MVC controllers are ever added, but the load-bearing surface for minimal APIs is this
/// per-endpoint filter.</para>
///
/// <para><b>Wire-shape contract.</b> <see cref="Results.ValidationProblem(IDictionary{string, string[]},string?,string?,int?,string?,string?,IDictionary{string, object?}?)"/>
/// emits the RFC 7807 envelope the issue spec mandates: <c>type</c>, <c>title</c>,
/// <c>status</c>, and an <c>errors</c> dictionary keyed by camel-cased property name. Property
/// names are camel-cased here (not by ASP.NET Core) because FluentValidation's
/// <c>ValidationFailure.PropertyName</c> mirrors the C# property casing — keeping the wire field
/// names aligned with the JSON request body shape that callers actually submit.</para>
///
/// <para><b>Belt-and-suspenders.</b> This filter rejects shape-level violations (empty path,
/// length cap, traversal token) BEFORE the handler runs, but path validation in the handler via
/// <see cref="Strg.Plugin.Abstractions.Storage.StoragePath.Parse"/> is intentionally retained. A future
/// non-HTTP caller (e.g. internal CLI, GraphQL mutation that bypasses this filter) still goes
/// through <c>Parse</c>; the filter is the front-door enforcement, <c>Parse</c> is the
/// last-line-of-defence guard.</para>
///
/// <para><b>Missing-validator policy.</b> A missing <see cref="IValidator{TRequest}"/>
/// registration is treated as a configuration bug: the filter is wired explicitly per route, so
/// a forgotten DI registration would otherwise ship to production with zero shape-level
/// enforcement. The filter throws <see cref="InvalidOperationException"/> on the first request
/// instead — surfacing the misconfig loudly via the global problem-details middleware rather
/// than silently relying on <c>StoragePath.Parse</c> to catch what a deleted validator would
/// have rejected.</para>
/// </summary>
public sealed class ValidationProblemDetailsFilter<TRequest> : IEndpointFilter
    where TRequest : class
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        // Locate the request body in the bound argument list. Minimal API parameter binding
        // produces the body as one of the arguments at a position the framework picks; OfType
        // avoids a brittle index assumption and tolerates additional arguments (route values,
        // injected services). A null result means the route does not bind a TRequest body
        // (e.g. only route values / query parameters) — pass through.
        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();
        if (request is null)
        {
            return await next(context);
        }

        var validator = context.HttpContext.RequestServices.GetService<IValidator<TRequest>>()
            ?? throw new InvalidOperationException(
                $"No IValidator<{typeof(TRequest).Name}> is registered. " +
                $"ValidationProblemDetailsFilter<{typeof(TRequest).Name}> requires a corresponding validator " +
                "to be registered via AddValidatorsFromAssemblyContaining<>(). Remove the filter " +
                "from the endpoint or add the missing validator.");

        var result = await validator.ValidateAsync(request, context.HttpContext.RequestAborted);
        if (result.IsValid)
        {
            return await next(context);
        }

        var errors = result.Errors
            .GroupBy(e => CamelCase(e.PropertyName))
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.ErrorMessage).ToArray());

        return Results.ValidationProblem(
            errors,
            title: "Validation failed",
            type: "https://tools.ietf.org/html/rfc7807");
    }

    private static string CamelCase(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            return propertyName;
        }

        if (!char.IsUpper(propertyName[0]))
        {
            return propertyName;
        }

        return char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
    }
}
