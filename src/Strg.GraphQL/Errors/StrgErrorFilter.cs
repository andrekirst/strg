using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Strg.Core.Exceptions;
using Strg.Core.Storage;

namespace Strg.GraphQL.Errors;

public sealed class StrgErrorFilter(IHostEnvironment env, ILogger<StrgErrorFilter> logger) : IErrorFilter
{
    public IError OnError(IError error)
    {
        return error.Exception switch
        {
            StoragePathException ex => Clean(error, "INVALID_PATH", ex.Message),
            ValidationException ex => Clean(error, "VALIDATION_ERROR", ex.Message),
            UnauthorizedAccessException => Clean(error, "FORBIDDEN", "Access denied."),
            NotFoundException ex => Clean(error, "NOT_FOUND", ex.Message),
            QuotaExceededException => Clean(error, "QUOTA_EXCEEDED", "Storage quota exceeded."),
            DuplicateDriveNameException ex => Clean(error, "DUPLICATE_DRIVE_NAME", ex.Message),
            _ => HandleUnexpected(error)
        };
    }

    private IError HandleUnexpected(IError error)
    {
        if (error.Exception is not null)
        {
            logger.LogError(error.Exception, "Unhandled GraphQL error");
        }

        if (env.IsDevelopment())
        {
            return error.WithCode("INTERNAL_ERROR");
        }

        return Clean(error, "INTERNAL_ERROR", "An internal error occurred.");
    }

    private static IError Clean(IError error, string code, string message)
        => ErrorBuilder.New()
            .SetCode(code)
            .SetMessage(message)
            .SetPath(error.Path)
            .Build();
}
