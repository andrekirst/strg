using System.Reflection;
using FluentValidation;
using Mediator;
using Strg.Core;
using StrgValidationException = Strg.Core.Exceptions.ValidationException;

namespace Strg.Application.Behaviors;

/// <summary>
/// Runs every registered <see cref="IValidator{TMessage}"/> before the handler. On failure the
/// behavior short-circuits to <see cref="Result.Failure"/> (or <see cref="Result{T}.Failure"/>)
/// when <typeparamref name="TResponse"/> is a Result shape — matching the convention that
/// commands surface expected failure modes as Result codes. When the response type is not a
/// Result, a <see cref="Strg.Core.Exceptions.ValidationException"/> is thrown instead so the
/// existing StrgErrorFilter / RFC 7807 mappings apply unchanged.
/// </summary>
public sealed class ValidationBehavior<TMessage, TResponse>(IEnumerable<IValidator<TMessage>> validators)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    private const string ValidationErrorCode = "ValidationError";

    // Evaluated once per (TMessage, TResponse) instantiation. Generic-type static fields are
    // per-closed-type, so the reflection cost is paid once per command shape at JIT time.
    private static readonly Func<string, string, TResponse>? ResultFailureFactory = BuildResultFailureFactory();

    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        var validatorList = validators as IReadOnlyCollection<IValidator<TMessage>> ?? [.. validators];
        if (validatorList.Count == 0)
        {
            return await next(message, cancellationToken);
        }

        var context = new ValidationContext<TMessage>(message);
        var results = await Task.WhenAll(validatorList.Select(v => v.ValidateAsync(context, cancellationToken)));
        var failures = results.SelectMany(r => r.Errors).Where(f => f is not null).ToArray();

        if (failures.Length == 0)
        {
            return await next(message, cancellationToken);
        }

        var errorMessage = string.Join("; ", failures.Select(f => $"{f.PropertyName}: {f.ErrorMessage}"));

        if (ResultFailureFactory is not null)
        {
            return ResultFailureFactory(ValidationErrorCode, errorMessage);
        }

        throw new StrgValidationException(errorMessage, failures[0].PropertyName);
    }

    private static Func<string, string, TResponse>? BuildResultFailureFactory()
    {
        var responseType = typeof(TResponse);

        if (responseType == typeof(Result))
        {
            return static (code, msg) => (TResponse)(object)Result.Failure(code, msg);
        }

        if (responseType.IsGenericType && responseType.GetGenericTypeDefinition() == typeof(Result<>))
        {
            var failureMethod = responseType.GetMethod(
                nameof(Result<object>.Failure),
                BindingFlags.Public | BindingFlags.Static);

            if (failureMethod is null)
            {
                return null;
            }

            return (code, msg) => (TResponse)failureMethod.Invoke(null, [code, msg])!;
        }

        return null;
    }
}
