---
id: STRG-056
title: Implement StrgErrorFilter for GraphQL error mapping
milestone: v0.1
priority: high
status: open
type: implementation
labels: [graphql, errors]
depends_on: [STRG-049]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-056: Implement StrgErrorFilter for GraphQL error mapping

## Summary

Implement `StrgErrorFilter` that maps domain exceptions to structured GraphQL errors with `extensions.code`. Production errors must not expose stack traces. Development mode includes stack traces for debugging.

## Technical Specification

### File: `src/Strg.GraphQL/Errors/StrgErrorFilter.cs`

```csharp
public sealed class StrgErrorFilter : IErrorFilter
{
    private readonly IHostEnvironment _env;
    private readonly ILogger<StrgErrorFilter> _logger;

    public StrgErrorFilter(IHostEnvironment env, ILogger<StrgErrorFilter> logger)
    {
        _env = env;
        _logger = logger;
    }

    public IError OnError(IError error)
    {
        var exception = error.Exception;

        return exception switch
        {
            StoragePathException => error
                .WithCode("INVALID_PATH")
                .RemoveException()
                .WithMessage(exception.Message),

            ValidationException ve => error
                .WithCode("VALIDATION_ERROR")
                .RemoveException()
                .WithMessage(ve.Message),

            UnauthorizedAccessException => error
                .WithCode("FORBIDDEN")
                .RemoveException()
                .WithMessage("Access denied."),

            NotFoundException => error
                .WithCode("NOT_FOUND")
                .RemoveException()
                .WithMessage(exception.Message),

            QuotaExceededException => error
                .WithCode("QUOTA_EXCEEDED")
                .RemoveException()
                .WithMessage("Storage quota exceeded."),

            DuplicateDriveNameException => error
                .WithCode("DUPLICATE_DRIVE_NAME")
                .RemoveException()
                .WithMessage(exception.Message),

            _ => HandleUnexpected(error, exception)
        };
    }

    private IError HandleUnexpected(IError error, Exception? ex)
    {
        if (ex is not null)
            _logger.LogError(ex, "Unhandled GraphQL error");

        return _env.IsDevelopment()
            ? error.WithCode("INTERNAL_ERROR")
            : error
                .WithCode("INTERNAL_ERROR")
                .RemoveException()
                .WithMessage("An internal error occurred.");
    }
}
```

### Error code table:

| Exception | GraphQL `extensions.code` |
|---|---|
| `StoragePathException` | `INVALID_PATH` |
| `ValidationException` | `VALIDATION_ERROR` |
| `UnauthorizedAccessException` | `FORBIDDEN` |
| `NotFoundException` | `NOT_FOUND` |
| `QuotaExceededException` | `QUOTA_EXCEEDED` |
| `DuplicateDriveNameException` | `DUPLICATE_DRIVE_NAME` |
| Any other | `INTERNAL_ERROR` |

### Registration order (STRG-049):

```csharp
.AddErrorFilter<StrgErrorFilter>() // BEFORE types
.AddQueryType(...)
```

## Acceptance Criteria

- [ ] `StoragePathException` → GraphQL error with `code: "INVALID_PATH"`
- [ ] Unhandled exception in production → `code: "INTERNAL_ERROR"`, no stack trace
- [ ] Unhandled exception in development → full exception details (for debugging)
- [ ] `FORBIDDEN` error has no details about the internal check (only "Access denied.")
- [ ] All known exceptions have their stack trace removed in production
- [ ] Unknown exceptions logged at `Error` level

## Test Cases

- **TC-001**: `StoragePathException` → `{ errors: [{ extensions: { code: "INVALID_PATH" } }] }`
- **TC-002**: Unhandled `NullReferenceException` in prod → no `exception` in response
- **TC-003**: Unhandled exception → `ILogger.LogError` called
- **TC-004**: `QuotaExceededException` → `code: "QUOTA_EXCEEDED"`

## Implementation Tasks

- [ ] Create `StrgErrorFilter.cs` in `Strg.GraphQL/Errors/`
- [ ] Create domain exception types: `NotFoundException`, `QuotaExceededException`, `DuplicateDriveNameException` in `Strg.Core/Exceptions/`
- [ ] Register filter in Hot Chocolate setup (before types)

## Testing Tasks

- [ ] Unit test: each exception type → correct `code`
- [ ] Integration test: production mode → stack trace absent from response

## Security Review Checklist

- [ ] Stack traces not present in production responses
- [ ] Generic message for `INTERNAL_ERROR` (no implementation details)

## Code Review Checklist

- [ ] Filter registered BEFORE type extensions (order matters in Hot Chocolate)
- [ ] `RemoveException()` called for all known exceptions in production

## Definition of Done

- [ ] All domain exceptions map to correct codes
- [ ] No stack traces in production responses
