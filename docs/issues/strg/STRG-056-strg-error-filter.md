---
id: STRG-056
title: Implement StrgErrorFilter for GraphQL error mapping
milestone: v0.1
priority: high
status: done
type: implementation
labels: [graphql, errors]
depends_on: [STRG-049]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-056: Implement StrgErrorFilter for GraphQL error mapping

## Summary

Implement `StrgErrorFilter` that maps domain exceptions to structured GraphQL errors with `extensions.code`. Also define the `UserError` type used in all mutation payload types. Production errors must not expose stack traces.

## Technical Specification

### `UserError` type (used in all mutation payloads)

```graphql
type UserError {
  code: String!
  message: String!
  field: String   # populated from ValidationException.PropertyName — enables form-field highlighting
}
```

C# record in `src/Strg.GraphQL/Payloads/UserError.cs`:

```csharp
public sealed record UserError(string Code, string Message, string? Field);
```

The `field` property maps directly to the input field name (e.g., `"name"`, `"path"`, `"email"`). Clients use this to highlight the specific form field that caused the error.

### `StrgErrorFilter` (maps domain exceptions to `errors` array codes)

File: `src/Strg.GraphQL/Errors/StrgErrorFilter.cs`

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

### Error code table

| Exception | `extensions.code` | `message` |
|---|---|---|
| `StoragePathException` | `INVALID_PATH` | exception.Message |
| `ValidationException` | `VALIDATION_ERROR` | exception.Message |
| `UnauthorizedAccessException` | `FORBIDDEN` | "Access denied." |
| `NotFoundException` | `NOT_FOUND` | exception.Message |
| `QuotaExceededException` | `QUOTA_EXCEEDED` | "Storage quota exceeded." |
| `DuplicateDriveNameException` | `DUPLICATE_DRIVE_NAME` | exception.Message |
| Any other | `INTERNAL_ERROR` | Generic in prod; full in dev |

### Two error surfaces

The project exposes errors on two surfaces — implementers must know both:

1. **`StrgErrorFilter`** — maps unhandled exceptions that escape resolvers to the `errors` array. This is the safety net for unexpected failures.

2. **Mutation payload `errors: [UserError!]`** — typed errors returned explicitly from resolvers (e.g., validation failures, not-found on a specific resource). These are **not** thrown exceptions; resolvers catch domain exceptions and return `UserError` instances in the payload.

```csharp
// Pattern A: StrgErrorFilter catches this (unhandled path)
throw new StoragePathException("path traversal attempt");

// Pattern B: resolver returns typed error in payload (preferred for predictable business errors)
return new CreateFolderPayload(null, [new UserError("INVALID_PATH", ex.Message, "path")]);
```

### Registration (STRG-049):

```csharp
.AddErrorFilter<StrgErrorFilter>()  // registered before type extensions — order matters
```

`StrgErrorFilter` is auto-discovered by `AddTypes()` since it implements `IErrorFilter`. Registration via `AddErrorFilter<T>()` remains explicit because it controls registration order relative to types.

## Acceptance Criteria

- [ ] `StoragePathException` → `{ errors: [{ extensions: { code: "INVALID_PATH" } }] }`
- [ ] Unhandled exception in production → `code: "INTERNAL_ERROR"`, no stack trace
- [ ] Unhandled exception in development → full exception details
- [ ] `FORBIDDEN` error message is always "Access denied." (no implementation details)
- [ ] Unknown exceptions logged at `Error` level via `ILogger`
- [ ] Mutation payloads use `UserError` with `field` populated where applicable

## Test Cases

- **TC-001**: `StoragePathException` → `errors[0].extensions.code = "INVALID_PATH"`
- **TC-002**: Unhandled `NullReferenceException` in prod → no `exception` in response
- **TC-003**: Unhandled exception → `ILogger.LogError` called
- **TC-004**: `QuotaExceededException` → `code: "QUOTA_EXCEEDED"`
- **TC-005**: `ValidationException` with `PropertyName = "name"` → `UserError.field = "name"` in payload

## Implementation Tasks

- [ ] Create `UserError.cs` record in `src/Strg.GraphQL/Payloads/`
- [ ] Create `StrgErrorFilter.cs` in `src/Strg.GraphQL/Errors/`
- [ ] Create domain exception types in `Strg.Core/Exceptions/`: `NotFoundException`, `QuotaExceededException`, `DuplicateDriveNameException`
- [ ] Register filter explicitly in HC setup with `AddErrorFilter<StrgErrorFilter>()` (before `AddTypes`)

## Security Review Checklist

- [ ] Stack traces not present in production responses
- [ ] Generic message for `INTERNAL_ERROR` (no implementation details leak)
- [ ] `FORBIDDEN` never reveals which check failed

## Code Review Checklist

- [ ] `StrgErrorFilter` registered BEFORE `AddTypes()` (order matters in Hot Chocolate)
- [ ] `RemoveException()` called for all known exceptions
- [ ] `UserError` is a `sealed record` (immutable)

## Definition of Done

- [ ] All domain exceptions map to correct codes
- [ ] No stack traces in production responses
- [ ] `UserError` type available in all mutation payloads
